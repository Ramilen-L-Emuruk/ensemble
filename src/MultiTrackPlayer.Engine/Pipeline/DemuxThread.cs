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
    private bool _hasPendingSeek;
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
        lock (_seekLock) { _pendingSeekTarget = targetSeconds; _hasPendingSeek = true; }
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
            if (TryTakePendingSeek(out double target))
            {
                PerformSeek(target);
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
                _eofReached = true;
                _videoQueue.PutEof(_videoQueue.Serial);
                _audioQueue.PutEof(_audioQueue.Serial);
                continue;
            }

            bool routed = RoutePacket(pkt.Packet);
            av_packet_unref(pkt.Packet);
            if (!routed)
            {
                if (_videoQueue.IsClosed || _audioQueue.IsClosed) break; // 終了処理中
                continue; // Put がシーク割込みで中断された: このパケットは捨ててループ先頭で保留シークを処理する
            }
        }
    }

    private bool TryTakePendingSeek(out double target)
    {
        lock (_seekLock)
        {
            if (!_hasPendingSeek) { target = 0; return false; }
            target = _pendingSeekTarget;
            _hasPendingSeek = false;
            return true;
        }
    }

    private void PerformSeek(double targetSeconds)
    {
        double offset = double.IsNaN(PtsSyncOffset) ? 0.0 : PtsSyncOffset;
        long ts = (long)((targetSeconds + offset) * AV_TIME_BASE);
        avformat_seek_file(_fmtCtx, -1, long.MinValue, ts, ts, (int)AVSEEK_FLAG.Backward);

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
