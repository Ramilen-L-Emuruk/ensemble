using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Engine.Pipeline;

public readonly struct AudioPacketRef
{
    public readonly IntPtr Packet;
    public readonly int TrackIndex;
    public AudioPacketRef(IntPtr packet, int trackIndex) { Packet = packet; TrackIndex = trackIndex; }
}

/// <summary>
/// 全音声トラックのパケットを track index 付きで単一キューに投入する。
/// トラック別に独立したキューにすると AudioDecodeThread が複数キューを同時待機する必要が生じるが、
/// BoundedSerialQueue はブロッキング Get が単一キューのみ対応のため、
/// 単一スレッドが単一キューを駆動する構成に単純化している（デッドロック源を避ける）。
/// </summary>
public unsafe sealed class AudioPacketQueue
{
    private readonly BoundedSerialQueue<AudioPacketRef> _queue;

    public AudioPacketQueue(int maxCount, int maxBytes)
    {
        _queue = new BoundedSerialQueue<AudioPacketRef>(
            maxCount, maxBytes,
            weigh: r => PacketOwnership.SizeOf((AVPacket*)r.Packet),
            disposer: r => PacketOwnership.Release((AVPacket*)r.Packet));
    }

    public int Serial => _queue.Serial;
    public bool IsClosed => _queue.IsClosed;
    public bool Close() => _queue.Close();
    public int Flush() => _queue.Flush();
    public void PutEof(int serial) => _queue.PutEof(serial);

    public bool PutMove(AVPacket* src, int trackIndex, int serial)
    {
        AVPacket* owned = PacketOwnership.AcquireCopy(src);
        bool ok = _queue.Put(new AudioPacketRef((IntPtr)owned, trackIndex), serial);
        if (!ok) PacketOwnership.Release(owned);
        return ok;
    }

    public bool Get(out QueueItem<AudioPacketRef> item) => _queue.Get(out item);

    public void DrainAndDispose()
    {
        while (_queue.Get(out var item))
            if (item.Kind == QueueItemKind.Data)
                PacketOwnership.Release((AVPacket*)item.Value.Packet);
    }
}
