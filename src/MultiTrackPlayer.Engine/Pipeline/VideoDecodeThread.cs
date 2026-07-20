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

    private volatile bool _stopRequested;
    private readonly object _seekTargetLock = new();
    private double _nextSeekTarget = double.NaN;
    private bool _prerollActive;
    private double _prerollTarget;
    // BeginWrite が SlotFlushed を返した（＝シークが発生した）後、FlushMarker に到達するまでの
    // 残りフレームは全てシーク前の残骸なので、4K変換を行わずに捨てる
    private bool _abandonUntilFlush;

    public VideoDecodeThread(VideoDecoder decoder, VideoPacketQueue queue, VideoFrameRing ring,
        Func<double> getPtsSyncOffset, double frameDurationSeconds)
    {
        _decoder = decoder;
        _queue = queue;
        _ring = ring;
        _getPtsSyncOffset = getPtsSyncOffset;
        _frameDurationSeconds = frameDurationSeconds;
    }

    /// <summary>DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと（happens-before の担保に必要）。</summary>
    public void SetSeekTarget(double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _nextSeekTarget = normalizedTargetSeconds;
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
                        HandleFlush();
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

    private void HandleFlush()
    {
        _decoder.FlushBuffers();
        // demux スレッドがシーク時に既に ring.Flush 済み（デッドロック解消のため）。
        // ここでもう一度呼び、demux の Flush 後にコミットされ得た残骸 Ready も掃除する
        _ring.Flush();
        _abandonUntilFlush = false;
        lock (_seekTargetLock)
        {
            _prerollActive = !double.IsNaN(_nextSeekTarget);
            _prerollTarget = _nextSeekTarget;
            _nextSeekTarget = double.NaN;
        }
        Diagnostics.DiagnosticLog.Write("video", $"flush 処理 preroll={( _prerollActive ? _prerollTarget.ToString("F3") : "なし")}");
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
            _prerollActive = false;
            Diagnostics.DiagnosticLog.Write("video", $"preroll 完了 firstPts={normalizedPts:F3}");
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
