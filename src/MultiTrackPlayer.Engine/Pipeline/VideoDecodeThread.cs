using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Video;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>映像codec ctxを専有し、パケットキューから受け取ってプリロール判定・変換・リング投入までを行う。</summary>
public sealed unsafe class VideoDecodeThread
{
    private readonly VideoDecoder _decoder;
    private readonly VideoPacketQueue _queue;
    private readonly VideoFrameRing _ring;
    private readonly Func<double> _getPtsSyncOffset;
    private readonly double _frameDurationSeconds;
    private readonly Action? _onFirstFrameAfterFlush;

    private volatile bool _stopRequested;
    private readonly object _seekTargetLock = new();
    // Flush 番兵1個につき1個の目標値が対応する FIFO。単一フィールドだと、このスレッドの処理が
    // 追いつかないまま2回連続でシークされた際に後発の SetSeekTarget が先発の値を上書きしてしまい、
    // 番兵と目標の対応がズレて片方の生成が「目標なし」のまま一生プリロール完了しなくなるバグがあった
    // （スキップ連打で HoldOutput が解除されず再生が固まる不具合の原因）
    private readonly Queue<double> _pendingSeekTargets = new();
    private bool _prerollActive;
    private double _prerollTarget;
    // このプリロールが属するキュー世代（Flush 番兵自身の Serial）。プリロール完了判定の瞬間に
    // 現在のキュー Serial と比較することで、既に次のシークに割り込まれた「無効な世代の完了通知」を検出する
    private int _prerollSerial;
    // BeginWrite が SlotFlushed を返した（＝シークが発生した）後、FlushMarker に到達するまでの
    // 残りフレームは全てシーク前の残骸なので、4K変換を行わずに捨てる
    private bool _abandonUntilFlush;

    public VideoDecodeThread(VideoDecoder decoder, VideoPacketQueue queue, VideoFrameRing ring,
        Func<double> getPtsSyncOffset, double frameDurationSeconds, Action? onFirstFrameAfterFlush = null)
    {
        _decoder = decoder;
        _queue = queue;
        _ring = ring;
        _getPtsSyncOffset = getPtsSyncOffset;
        _frameDurationSeconds = frameDurationSeconds;
        _onFirstFrameAfterFlush = onFirstFrameAfterFlush;
    }

    /// <summary>DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと（happens-before の担保に必要）。</summary>
    public void SetSeekTarget(double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _pendingSeekTargets.Enqueue(normalizedTargetSeconds);
    }

    public void RequestStop() => _stopRequested = true;

    public void Run()
    {
        AVFrame* frame = av_frame_alloc();
        try
        {
            while (!_stopRequested)
            {
                if (!_queue.Get(out var item)) break; // Close 済み

                switch (item.Kind)
                {
                    case QueueItemKind.Flush:
                        HandleFlush(item.Serial);
                        break;
                    case QueueItemKind.Eof:
                        HandleEof(frame);
                        break;
                    case QueueItemKind.Data:
                        var pkt = (AVPacket*)item.Value;
                        try { HandlePacket(pkt, frame); }
                        finally { PacketOwnership.Release(pkt); }
                        break;
                }
            }
        }
        finally
        {
            av_frame_free(&frame);
        }
    }

    private void HandleFlush(int serial)
    {
        _decoder.FlushBuffers();
        // demux スレッドがシーク時に既に ring.Flush 済み（デッドロック解消のため）。
        // ここでもう一度呼び、demux の Flush 後にコミットされ得た残骸 Ready も掃除する
        _ring.Flush();
        _abandonUntilFlush = false;
        _prerollSerial = serial;
        lock (_seekTargetLock)
        {
            _prerollActive = _pendingSeekTargets.Count > 0;
            _prerollTarget = _prerollActive ? _pendingSeekTargets.Dequeue() : double.NaN;
        }
        Diagnostics.DiagnosticLog.Write("video", $"flush 処理 serial={serial} preroll={( _prerollActive ? _prerollTarget.ToString("F3") : "なし")}");
    }

    private void HandleEof(AVFrame* frame)
    {
        _decoder.SendPacket(null);
        DrainAvailable(frame);
        _ring.MarkEof();
    }

    private void HandlePacket(AVPacket* pkt, AVFrame* frame)
    {
        int ret = _decoder.SendPacket(pkt);
        while (ret == -EAGAIN)
        {
            DrainAvailable(frame);
            ret = _decoder.SendPacket(pkt);
        }
        DrainAvailable(frame);
    }

    private void DrainAvailable(AVFrame* frame)
    {
        while (_decoder.TryReceiveFrame(frame))
        {
            EmitFrame(frame);
            av_frame_unref(frame);
        }
    }

    private void EmitFrame(AVFrame* frame)
    {
        if (_abandonUntilFlush) return; // シーク発生後の残骸フレーム（FlushMarker 到達まで捨てる）

        double offset = _getPtsSyncOffset();
        if (double.IsNaN(offset)) return; // demux 側で確定前（通常は先に確定している）

        double normalizedPts = _decoder.GetPtsSeconds(frame) - offset;

        if (_prerollActive)
        {
            if (normalizedPts < _prerollTarget - _frameDurationSeconds / 2.0)
                return; // hw転送・sws変換前に破棄（4Kの33MB転送を丸ごと省く）

            // プリロール完了と判定できたが、その間に次のシークが割り込んでキューの Serial が
            // 既に進んでいる場合、これは無効な世代の完了通知（このデコードスレッドがまだ
            // 新しい Flush 番兵に到達していないだけ）。コールバックを発火せず残骸として捨てる。
            // 発火してしまうと MediaEngine 側が古いシーク目標を「映像プリロール完了」と誤認し、
            // 音声側が別世代で先に完了していた場合にミキサーの保留を誤って解除してしまう
            // （巻き戻し連打時に稀に発生する早送り/大量ドロップの原因）
            if (_queue.Serial != _prerollSerial)
            {
                _abandonUntilFlush = true;
                Diagnostics.DiagnosticLog.Write("video", $"stale preroll 破棄 prerollSerial={_prerollSerial} currentSerial={_queue.Serial} pts={normalizedPts:F3}");
                return;
            }

            _prerollActive = false;
            Diagnostics.DiagnosticLog.Write("video", $"preroll 完了 firstPts={normalizedPts:F3} serial={_prerollSerial}");
            // シーク後、映像プリロールがここで完了する。MediaEngine 側はこれを合図に
            // ミキサーの音声出力保留（HoldOutput）を解除する（早送りバグの根治）
            _onFirstFrameAfterFlush?.Invoke();
        }

        int slot = _ring.BeginWrite(frame->width, frame->height);
        if (slot == VideoFrameRing.SlotClosed) return;
        if (slot == VideoFrameRing.SlotFlushed) { _abandonUntilFlush = true; return; }

        IntPtr dst = _ring.GetWriteBuffer(slot);
        int stride = frame->width * 4;
        if (_decoder.ConvertInto(frame, dst, stride, out _, out _))
            _ring.CommitWrite(slot, normalizedPts);
        else
            _ring.AbortWrite(slot);
    }
}
