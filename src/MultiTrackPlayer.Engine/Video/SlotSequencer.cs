namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// 固定長スロットの状態機械（Free/Writing/Ready/Leased）と serial 世代・Monitor 待機・Flush・EOF・
/// Close・due 選択（<see cref="FrameSelector"/> 委譲）を担う汎用コア。ペイロード（ネイティブバッファや
/// GPU テクスチャ）は保持せず、呼び出し側がスロット index に対応づけて管理する。ペイロードの確保・解放は
/// クリティカルセクション内で呼ばれるコールバックで行い、状態遷移との原子性を保つ。
/// </summary>
public sealed class SlotSequencer
{
    /// <summary>BeginWrite の戻り値: Close 済み。</summary>
    public const int SlotClosed = -1;
    /// <summary>BeginWrite の戻り値: 空き待ち中に Flush が発生（このフレームは破棄すべき）。</summary>
    public const int SlotFlushed = -2;

    private enum SlotState { Free, Writing, Ready, Leased }

    private sealed class Slot
    {
        public SlotState State = SlotState.Free;
        public double PtsSeconds;
        public int Serial;
        public bool PendingFreeOnReturn;
    }

    private readonly Slot[] _slots;
    private readonly object _lock = new();
    private bool _closed;
    private bool _eofMarked;
    // Flush ごとに増える世代番号。古い世代のフレームの CommitWrite は棄却される
    private int _serial;

    public SlotSequencer(int slotCount)
    {
        _slots = new Slot[slotCount];
        for (int i = 0; i < slotCount; i++) _slots[i] = new Slot();
    }

    public int SlotCount => _slots.Length;

    public int CurrentSerial { get { lock (_lock) return _serial; } }

    /// <summary>
    /// Free スロットが空くまでブロックし、確保できたら <paramref name="onAcquired"/> をロック内で呼んで
    /// から Writing に遷移させて index を返す。ペイロードの確保はこのコールバック内で行うことで状態遷移と
    /// 同一クリティカルセクションに収める。Close 済みなら <see cref="SlotClosed"/>、待機中に Flush が起きたら
    /// <see cref="SlotFlushed"/>（呼び出し側はこのフレームを破棄する）。
    /// </summary>
    public int BeginWrite(Action<int> onAcquired)
    {
        lock (_lock)
        {
            int entrySerial = _serial;
            int idx;
            while ((idx = FindFreeSlotLocked()) < 0 && !_closed && _serial == entrySerial)
                Monitor.Wait(_lock);
            if (_closed) return SlotClosed;
            if (_serial != entrySerial) return SlotFlushed;

            onAcquired(idx);
            var slot = _slots[idx];
            slot.Serial = _serial;
            slot.State = SlotState.Writing;
            return idx;
        }
    }

    public void CommitWrite(int slotIndex, double ptsSeconds)
    {
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            // 変換中に Flush が起きた（＝シーク前のフレーム）場合は Ready にせず破棄する
            if (slot.Serial != _serial)
            {
                slot.State = SlotState.Free;
                Monitor.PulseAll(_lock);
                return;
            }
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
    /// クロック位置に対して due な最新スロットを1枚リースする。選ばれなかった古い Ready は破棄され
    /// <paramref name="droppedCount"/> に計上される。何も due でなければ false。選ばれたスロットは Leased に
    /// なるため、呼び出し側が <see cref="ReturnLease"/> するまでそのペイロードは他スレッドから変更されない。
    /// </summary>
    public bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds,
        out int slotIndex, out double ptsSeconds, out int droppedCount)
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
            {
                slotIndex = -1;
                ptsSeconds = 0;
                return false;
            }

            double chosenPts = _slots[chosen].PtsSeconds;
            bool freedAny = false;
            foreach (var c in candidates)
                if (c.SlotIndex != chosen && c.Pts <= chosenPts)
                {
                    _slots[c.SlotIndex].State = SlotState.Free;
                    freedAny = true;
                }

            _slots[chosen].State = SlotState.Leased;
            // drop で Free になったスロットを、Free 待ちでブロック中の BeginWrite（デコードスレッド）へ通知する。
            // これが無いと、一時停止中にリングが満杯化 → 再生再開後の drop で Free ができても寝ている BeginWrite が
            // 起床せず、デコードが再開しないまま映像が固まる（他の状態遷移は PulseAll するのに TryLeaseDue だけ抜けていた）。
            if (freedAny) Monitor.PulseAll(_lock);
            slotIndex = chosen;
            ptsSeconds = _slots[chosen].PtsSeconds;
            return true;
        }
    }

    /// <summary>
    /// 最も古い Ready スロットを1枚リースする（クロック非依存。Step・一時停止中シーク用）。
    /// minSerial 未満の世代（＝シーク前の残骸）は対象外。timeout 内に無ければ false。
    /// </summary>
    public bool TryLeaseOldest(TimeSpan timeout, int minSerial, out int slotIndex, out double ptsSeconds)
    {
        lock (_lock)
        {
            var deadline = DateTime.UtcNow + timeout;
            int chosen;
            while ((chosen = FindOldestReadyLocked(minSerial)) < 0 && !_closed)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) { slotIndex = -1; ptsSeconds = 0; return false; }
                Monitor.Wait(_lock, remaining);
            }
            if (chosen < 0) { slotIndex = -1; ptsSeconds = 0; return false; }

            _slots[chosen].State = SlotState.Leased;
            slotIndex = chosen;
            ptsSeconds = _slots[chosen].PtsSeconds;
            return true;
        }
    }

    /// <summary>
    /// リース中のスロットを Free に戻す。Close 済みで PendingFreeOnReturn が立っていれば、ロック内で
    /// <paramref name="onFreePayload"/> を呼んで呼び出し側にペイロードを解放させる。リース中でなければ何もしない。
    /// </summary>
    public void ReturnLease(int slotIndex, Action<int> onFreePayload)
    {
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            if (slot.State != SlotState.Leased) return;

            if (slot.PendingFreeOnReturn)
            {
                onFreePayload(slotIndex);
                slot.PendingFreeOnReturn = false;
            }
            slot.State = SlotState.Free;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// シーク時: 世代番号を進めて Ready を Free に戻し、EOF 状態も解除する。BeginWrite で空き待ち中の
    /// デコーダは SlotFlushed で起床する。Writing スロットはデコーダが変換中のため触らない（CommitWrite が
    /// 世代不一致で自ら破棄する）。Leased スロットは呼び出し側がまだ参照中の可能性があるため触らない。
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            _serial++;
            foreach (var slot in _slots)
                if (slot.State == SlotState.Ready)
                    slot.State = SlotState.Free;
            _eofMarked = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>producer が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof()
    {
        lock (_lock) { _eofMarked = true; Monitor.PulseAll(_lock); }
    }

    /// <summary>EOF 済みかつ表示待ち・リース中のスロットが残っていない（再生完了検出に使う）。</summary>
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

    /// <summary>
    /// Close 後の解放。Leased 中のスロットは呼び出し側がまだ参照している可能性があるため即座には解放せず、
    /// PendingFreeOnReturn を立てて <see cref="ReturnLease"/> 時に解放させる。それ以外のスロットはロック内で
    /// <paramref name="onFreePayload"/> を呼んで即解放させる。
    /// </summary>
    public void DisposeSlots(Action<int> onFreePayload)
    {
        lock (_lock)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].State == SlotState.Leased)
                {
                    _slots[i].PendingFreeOnReturn = true;
                    continue;
                }
                onFreePayload(i);
            }
        }
    }

    /// <summary>診断用: 全スロットの状態スナップショット（停止検知時のログ出力に使う）。</summary>
    public string DescribeSlots()
    {
        lock (_lock)
        {
            var parts = new string[_slots.Length];
            for (int i = 0; i < _slots.Length; i++)
            {
                var s = _slots[i];
                parts[i] = $"[{i}:{s.State} pts={s.PtsSeconds:F3} serial={s.Serial}]";
            }
            return $"serial={_serial} eof={_eofMarked} closed={_closed} {string.Join(" ", parts)}";
        }
    }

    private int FindFreeSlotLocked()
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].State == SlotState.Free) return i;
        return -1;
    }

    private int FindOldestReadyLocked(int minSerial)
    {
        int chosen = -1;
        double bestPts = double.MaxValue;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].State == SlotState.Ready && _slots[i].Serial >= minSerial &&
                _slots[i].PtsSeconds < bestPts)
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
}
