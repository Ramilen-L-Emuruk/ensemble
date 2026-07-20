using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>映像ストリームのパケットキュー。BoundedSerialQueue&lt;IntPtr&gt; の unsafe 薄ラッパ。</summary>
public unsafe sealed class VideoPacketQueue
{
    private readonly BoundedSerialQueue<IntPtr> _queue;

    public VideoPacketQueue(int maxCount, int maxBytes)
    {
        _queue = new BoundedSerialQueue<IntPtr>(
            maxCount, maxBytes,
            weigh: p => PacketOwnership.SizeOf((AVPacket*)p),
            disposer: p => PacketOwnership.Release((AVPacket*)p));
    }

    public int Serial => _queue.Serial;
    public bool IsClosed => _queue.IsClosed;
    public bool Close() => _queue.Close();
    public int Flush() => _queue.Flush();
    public void PutEof(int serial) => _queue.PutEof(serial);
    public void AbortPutWaiters() => _queue.AbortPutWaiters();

    /// <summary>src の中身の所有権を取って投入する。Close 済みで投入できなかった場合は自分で解放する。</summary>
    public bool PutMove(AVPacket* src, int serial)
    {
        AVPacket* owned = PacketOwnership.AcquireCopy(src);
        bool ok = _queue.Put((IntPtr)owned, serial);
        if (!ok) PacketOwnership.Release(owned);
        return ok;
    }

    public bool Get(out QueueItem<IntPtr> item) => _queue.Get(out item);

    /// <summary>Close 済み・スレッド join 済みの状態で呼ぶこと。残存パケットを解放する。</summary>
    public void DrainAndDispose()
    {
        while (_queue.Get(out var item))
            if (item.Kind == QueueItemKind.Data)
                PacketOwnership.Release((AVPacket*)item.Value);
    }
}
