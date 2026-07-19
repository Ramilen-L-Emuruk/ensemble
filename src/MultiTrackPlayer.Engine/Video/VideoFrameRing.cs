using System.Runtime.InteropServices;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// GPU→CPU 転送後の BGRA フレームを保持するネイティブメモリの固定長リング。
/// VideoDecodeThread（producer）が Free スロットへ書き込み、pacer/UI（consumer）が
/// due なフレームを取り出す。毎フレームの byte[] 確保を避けるためスロットのバッファは使い回す。
/// </summary>
public sealed class VideoFrameRing : IDisposable
{
    public delegate void FrameCopyCallback(IntPtr buffer, int width, int height, int stride, double ptsSeconds);

    private const int SlotCount = 4;

    private enum SlotState { Free, Writing, Ready }

    private sealed class Slot
    {
        public SlotState State = SlotState.Free;
        public IntPtr Buffer = IntPtr.Zero;
        public int Capacity;
        public int Width, Height, Stride;
        public double PtsSeconds;
    }

    private readonly Slot[] _slots;
    private readonly object _lock = new();
    private bool _closed;
    private bool _eofMarked;

    public VideoFrameRing()
    {
        _slots = new Slot[SlotCount];
        for (int i = 0; i < SlotCount; i++) _slots[i] = new Slot();
    }

    /// <summary>Free スロットが空くまでブロックする。Close 済みなら -1。</summary>
    public int BeginWrite(int width, int height)
    {
        lock (_lock)
        {
            int idx;
            while ((idx = FindFreeSlotLocked()) < 0 && !_closed)
                Monitor.Wait(_lock);
            if (_closed) return -1;

            var slot = _slots[idx];
            int stride = width * 4;
            int needed = stride * height;
            if (slot.Capacity < needed)
            {
                if (slot.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(slot.Buffer);
                slot.Buffer = Marshal.AllocHGlobal(needed);
                slot.Capacity = needed;
            }
            slot.Width = width;
            slot.Height = height;
            slot.Stride = stride;
            slot.State = SlotState.Writing;
            return idx;
        }
    }

    public IntPtr GetWriteBuffer(int slotIndex) { lock (_lock) return _slots[slotIndex].Buffer; }

    public void CommitWrite(int slotIndex, double ptsSeconds)
    {
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            slot.PtsSeconds = ptsSeconds;
            slot.State = SlotState.Ready;
            Monitor.PulseAll(_lock);
        }
    }

    public void AbortWrite(int slotIndex)
    {
        lock (_lock)
        {
            _slots[slotIndex].State = SlotState.Free;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// クロック位置に対して due な最新フレームを1枚取り出す。選ばれなかった古い Ready は破棄され
    /// droppedCount に計上される。何も due でなければ false（呼び出し側は次回ティックで再試行）。
    /// </summary>
    public bool TryConsumeDue(double clockPositionSeconds, double frameDurationSeconds, FrameCopyCallback copy, out int droppedCount)
    {
        lock (_lock)
        {
            var candidates = new List<CandidateFrame>();
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].State == SlotState.Ready)
                    candidates.Add(new CandidateFrame(i, _slots[i].PtsSeconds));
            candidates.Sort((a, b) => a.Pts.CompareTo(b.Pts));

            var selection = FrameSelector.SelectDue(candidates, clockPositionSeconds, frameDurationSeconds);
            droppedCount = selection.DroppedCount;
            if (selection.SlotIndex is not int chosen)
                return false;

            double chosenPts = _slots[chosen].PtsSeconds;
            foreach (var c in candidates)
                if (c.SlotIndex != chosen && c.Pts <= chosenPts)
                    _slots[c.SlotIndex].State = SlotState.Free;

            var slot = _slots[chosen];
            copy(slot.Buffer, slot.Width, slot.Height, slot.Stride, slot.PtsSeconds);
            slot.State = SlotState.Free;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>最も古い Ready フレームを1枚取り出す（クロック非依存。StepForward 用）。timeout 内に無ければ false。</summary>
    public bool TakeOldest(TimeSpan timeout, FrameCopyCallback copy)
    {
        lock (_lock)
        {
            var deadline = DateTime.UtcNow + timeout;
            int chosen;
            while ((chosen = FindOldestReadyLocked()) < 0 && !_closed)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(_lock, remaining);
            }
            if (chosen < 0) return false;

            var slot = _slots[chosen];
            copy(slot.Buffer, slot.Width, slot.Height, slot.Stride, slot.PtsSeconds);
            slot.State = SlotState.Free;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>シーク時: Writing/Ready を全て Free に戻し、EOF 状態も解除する。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            foreach (var slot in _slots)
                if (slot.State != SlotState.Free) slot.State = SlotState.Free;
            _eofMarked = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>VideoDecodeThread が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof()
    {
        lock (_lock) { _eofMarked = true; Monitor.PulseAll(_lock); }
    }

    /// <summary>EOF 済みかつ表示待ちフレームが残っていない（再生完了検出に使う）。</summary>
    public bool IsEofDrained
    {
        get { lock (_lock) return _eofMarked && AllFreeLocked(); }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }

    private int FindFreeSlotLocked()
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].State == SlotState.Free) return i;
        return -1;
    }

    private int FindOldestReadyLocked()
    {
        int chosen = -1;
        double bestPts = double.MaxValue;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].State == SlotState.Ready && _slots[i].PtsSeconds < bestPts)
            {
                chosen = i;
                bestPts = _slots[i].PtsSeconds;
            }
        }
        return chosen;
    }

    private bool AllFreeLocked()
    {
        foreach (var slot in _slots)
            if (slot.State != SlotState.Free) return false;
        return true;
    }

    public void Dispose()
    {
        Close();
        lock (_lock)
        {
            foreach (var slot in _slots)
            {
                if (slot.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(slot.Buffer);
                slot.Buffer = IntPtr.Zero;
                slot.Capacity = 0;
            }
        }
    }
}
