using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>
/// AVFormatContext を唯一専有するスレッド。read/seek は必ずここで行い、他スレッドからは触らない。
/// シーク要求は最新の1件のみ保持する（連打はコアレスされる）。
/// </summary>
public sealed unsafe class DemuxThread
{
    private readonly AVFormatContext* _fmtCtx;
    private readonly int _videoStreamIndex;
    private readonly IReadOnlyDictionary<int, int> _audioStreamToTrack;
    private readonly VideoPacketQueue _videoQueue;
    private readonly AudioPacketQueue _audioQueue;
    private readonly Action<double> _publishSeekTarget;
    private readonly ManualResetEventSlim _wakeEvent = new(false);

    private volatile bool _stopRequested;
    private volatile bool _eofReached;
    private double _ptsSyncOffset = double.NaN;

    private readonly object _seekLock = new();
    // シーク要求は「受け付けた通番」と「処理し終えた通番」の差で表す。
    // bool 1 つだと、要求を取り出してから実際にシークし終えるまでの間だけ
    // 「保留なし・EOF のまま」という状態が見え、その隙に Play() が終端と誤判定する
    private long _seekRequestSeq;
    private long _seekHandledSeq;
    private double _pendingSeekTarget;

    public bool EofReached => _eofReached;
    public double PtsSyncOffset => Volatile.Read(ref _ptsSyncOffset);

    public DemuxThread(
        AVFormatContext* fmtCtx, int videoStreamIndex, IReadOnlyDictionary<int, int> audioStreamToTrack,
        VideoPacketQueue videoQueue, AudioPacketQueue audioQueue, Action<double> publishSeekTarget)
    {
        _fmtCtx = fmtCtx;
        _videoStreamIndex = videoStreamIndex;
        _audioStreamToTrack = audioStreamToTrack;
        _videoQueue = videoQueue;
        _audioQueue = audioQueue;
        _publishSeekTarget = publishSeekTarget;
    }

    /// <summary>UI/呼び出しスレッドから即座に返る。保留中のシークがあれば最新の目標で上書きする。</summary>
    public void RequestSeek(double targetSeconds)
    {
        lock (_seekLock) { _pendingSeekTarget = targetSeconds; _seekRequestSeq++; }
        // demux スレッドが満杯キューの Put でブロック中だとシーク要求を永遠にチェックできない
        //（映像リング満杯→映像キュー満杯→demux ブロック→全パイプライン凍結、の実機で観測された連鎖）。
        // Put 待ちを中断させてループ先頭へ帰還させる
        _videoQueue.AbortPutWaiters();
        _audioQueue.AbortPutWaiters();
        _wakeEvent.Set();
    }

    public void RequestStop()
    {
        _stopRequested = true;
        _wakeEvent.Set();
    }

    public void Run()
    {
        using var pkt = new DemuxPacketHolder();
        while (!_stopRequested)
        {
            if (TryTakePendingSeek(out double target, out long seekSeq))
            {
                PerformSeek(target);
                // 完了を記録するのはシークし終えた後。先に記録すると、その隙間で
                // 「保留なし・EOF のまま」と見えて Play() が終端と誤判定する
                MarkSeekHandled(seekSeq);
                continue;
            }

            if (_eofReached)
            {
                // EOF 後は新しいコマンド（シーク/停止）まで読み進めずに待機する
                _wakeEvent.Wait();
                _wakeEvent.Reset();
                continue;
            }

            int ret = av_read_frame(_fmtCtx, pkt.Packet);
            if (ret < 0)
            {
                // 停止要求に伴う I/O 中断（AVIOInterruptCB）でも負値が返る。これをファイル終端として
                // 扱うと EOF 番兵が積まれ、停止操作が「再生完了」として扱われてしまう（プレイリストの
                // 自動送りが走る）。中断と終端は戻り値では区別できないため、停止要求の有無で分ける
                if (_stopRequested) break;
                _eofReached = true;
                _videoQueue.PutEof(_videoQueue.Serial);
                _audioQueue.PutEof(_audioQueue.Serial);
                continue;
            }

            bool routed = RoutePacket(pkt.Packet);
            av_packet_unref(pkt.Packet);
            if (!routed)
            {
                // 停止要求が出ている＝パイプライン全体を畳んでいるので終える。
                // それ以外でキューが閉じているのは、片側のデコードスレッドが異常終了してその側だけを
                // 畳んだ状態。ここで break すると AVFormatContext を専有するこのスレッドが消え、
                // 生き残った側への供給まで止まってしまう（映像・音声のどちらが落ちても同じなので
                // 対称に扱う）。そのパケットは捨てて読み進め、終端まで到達させる。
                // Put がシーク割込みで中断された場合も同じく捨て、ループ先頭で保留シークを処理する
                if (_stopRequested) break;
                continue;
            }
        }
    }

    /// <summary>
    /// シーク要求を抱えている、または処理の途中であるか。
    /// RequestSeek は要求を積んで起こすだけで、EofReached が false に戻るのは
    /// このスレッドが実際に PerformSeek を終えた後。その間に EofReached だけを見ると
    /// 「まだ終端にいる」と誤って判断してしまうため、判定側はこれも併せて見ること。
    /// 処理し終えるまで true を返し続けるので、取り出しから完了までの隙間も塞がる。
    /// </summary>
    public bool HasPendingSeek { get { lock (_seekLock) return _seekHandledSeq != _seekRequestSeq; } }

    private bool TryTakePendingSeek(out double target, out long seq)
    {
        lock (_seekLock)
        {
            if (_seekHandledSeq == _seekRequestSeq) { target = 0; seq = 0; return false; }
            target = _pendingSeekTarget;
            seq = _seekRequestSeq;
            return true; // ここでは完了扱いにしない（PerformSeek 完了時に MarkSeekHandled で進める）
        }
    }

    /// <summary>処理し終えたシーク要求の通番を進める。処理中に来た新しい要求は保留のまま残る。</summary>
    private void MarkSeekHandled(long seq)
    {
        lock (_seekLock) { if (_seekHandledSeq < seq) _seekHandledSeq = seq; }
    }

    private void PerformSeek(double targetSeconds)
    {
        double offset = double.IsNaN(PtsSyncOffset) ? 0.0 : PtsSyncOffset;
        long ts = (long)((targetSeconds + offset) * AV_TIME_BASE);
        int ret = avformat_seek_file(_fmtCtx, -1, long.MinValue, ts, ts, (int)AVSEEK_FLAG.Backward);
        if (ret < 0)
        {
            // 読み取り位置が変わらないまま以降の処理を続ける。目標より後ろを読んでいれば
            // いずれ到達して復帰し、到達しない場合も EOF 時にプリロールが解除される
            Diagnostics.DiagnosticLog.Write("demux", $"シークに失敗（現在位置のまま続行） target={targetSeconds:F3} ret={ret}");
        }

        // 各デコードスレッドの Flush 処理より前にプリロール目標を publish しておく必要がある
        // （Flush 番兵を受け取った時点で target が既に確定しているように、ロック経由の happens-before を利用する）
        _publishSeekTarget(targetSeconds);
        _videoQueue.Flush();
        _audioQueue.Flush();
        _eofReached = false;
    }

    private bool RoutePacket(AVPacket* pkt)
    {
        int idx = pkt->stream_index;

        if (double.IsNaN(Volatile.Read(ref _ptsSyncOffset)))
        {
            double abs = ComputePacketAbsSeconds(pkt);
            if (!double.IsNaN(abs) && (idx == _videoStreamIndex || _videoStreamIndex < 0))
                Volatile.Write(ref _ptsSyncOffset, abs);
        }

        if (idx == _videoStreamIndex)
            return _videoQueue.PutMove(pkt, _videoQueue.Serial);

        if (_audioStreamToTrack.TryGetValue(idx, out int trackIndex))
            return _audioQueue.PutMove(pkt, trackIndex, _audioQueue.Serial);

        return true; // 対象外ストリーム（字幕等）は無視
    }

    private double ComputePacketAbsSeconds(AVPacket* pkt)
    {
        if (pkt->pts == long.MinValue) return double.NaN; // AV_NOPTS_VALUE
        var stream = _fmtCtx->streams[pkt->stream_index];
        return pkt->pts * av_q2d(stream->time_base);
    }

    private sealed unsafe class DemuxPacketHolder : IDisposable
    {
        public AVPacket* Packet = av_packet_alloc();
        public void Dispose() { if (Packet != null) { AVPacket* p = Packet; av_packet_free(&p); } Packet = null; }
    }
}
