namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// 固定長スロットの状態機械（Free/Writing/Ready/Leased）と serial 世代・Monitor 待機・Flush・EOF・
/// Close・due 選択（<see cref="FrameSelector"/> 委譲）を担う汎用コア。ペイロード（ネイティブバッファや
/// GPU テクスチャ）は保持せず、呼び出し側がスロット index に対応づけて管理する。ペイロードの確保・解放は
/// クリティカルセクション内で呼ばれるコールバックで行い、状態遷移との原子性を保つ。
///
/// 解放コールバックは生成時に 1 つだけ受け取る。以前は解放しうるメソッドごとに引数で渡していたが、
/// それでは「解放しうる経路を新設したのに渡し忘れる」ことが型で防げない（実際に <see cref="DisposeSlots"/>
/// が Writing スロットを保護しない不具合を生んでいた）。
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
        // 破棄時にまだ他スレッドが参照していて即解放できなかったスロットに立つ。
        // 参照が離れる経路（Leased→ReturnLease / Writing→CommitWrite・AbortWrite）で解放する
        public bool PendingFreeOnRelease;
    }

    private readonly Slot[] _slots;
    private readonly Action<int> _onFreePayload;
    private readonly object _lock = new();
    private bool _closed;
    private bool _eofMarked;
    // Flush ごとに増える世代番号。古い世代のフレームの CommitWrite は棄却される
    private int _serial;

    /// <param name="slotCount">スロット数。</param>
    /// <param name="onFreePayload">
    /// スロット index に対応するペイロードを解放するコールバック。必ずロック内から呼ばれるため、
    /// この中で別のロックを取らないこと（デッドロック源）。
    /// </param>
    public SlotSequencer(int slotCount, Action<int> onFreePayload)
    {
        _onFreePayload = onFreePayload ?? throw new ArgumentNullException(nameof(onFreePayload));
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
        Exception? releaseError;
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            if (!TryReleaseDeferredLocked(slot, slotIndex, out releaseError))
            {
                // 変換中に Flush が起きた（＝シーク前のフレーム）場合は Ready にせず破棄する
                if (slot.Serial != _serial)
                {
                    slot.State = SlotState.Free;
                }
                else
                {
                    slot.PtsSeconds = ptsSeconds;
                    slot.State = SlotState.Ready;
                }
                Monitor.PulseAll(_lock);
            }
        }
        ReportReleaseFailure(slotIndex, releaseError);
    }

    public void AbortWrite(int slotIndex)
    {
        Exception? releaseError;
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            if (!TryReleaseDeferredLocked(slot, slotIndex, out releaseError))
            {
                slot.State = SlotState.Free;
                Monitor.PulseAll(_lock);
            }
        }
        ReportReleaseFailure(slotIndex, releaseError);
    }

    /// <summary>
    /// 遅延解放が予約されているスロットなら、ここでペイロードを解放して Free に戻す（true を返す）。
    /// Writing / Leased から抜ける全経路の先頭で呼ぶこと。呼ばない経路があると、破棄時に保護した
    /// ペイロードを誰も解放せず恒久リークになる。
    /// </summary>
    /// <param name="error">
    /// 解放コールバックが投げた例外（無ければ null）。記録はロックを抜けてから
    /// <see cref="ReportReleaseFailure"/> で行うこと。
    /// </param>
    private bool TryReleaseDeferredLocked(Slot slot, int slotIndex, out Exception? error)
    {
        error = null;
        if (!slot.PendingFreeOnRelease) return false;
        // 予約は「消化を試みた」時点で必ず降ろし、状態も Free へ進める。解放が失敗したときに
        // 予約を残すと、次に同じスロットへ来た呼び出しがもう一度解放を試みて二重解放になり、
        // かつスロットが Free に戻らないまま 4 枠のうち 1 枠が恒久的に失われる
        //（GPU 経路の ID3D11Texture2D.Dispose がデバイスロスト時に投げうる）。
        // 解放できなかったペイロードはリークするが、それは検疫と同じ扱いで許容する
        slot.PendingFreeOnRelease = false;
        slot.State = SlotState.Free;
        try
        {
            _onFreePayload(slotIndex);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Monitor.PulseAll(_lock);
        return true;
    }

    /// <summary>
    /// ペイロード解放の失敗を記録する。**必ずロックを解放してから呼ぶこと。**
    /// <c>DiagnosticLog.WriteFatal</c> はデバッグモード無効時（既定）にプロセス間ミューテックスの
    /// 待機とファイル I/O を伴うため、クリティカルセクション内で呼ぶと <see cref="Monitor.Wait"/> で
    /// 待っているデコードスレッド・vout スレッド・UI を数百ms 単位で足止めする。
    /// </summary>
    private static void ReportReleaseFailure(int slotIndex, Exception? error)
    {
        if (error == null) return;
        Diagnostics.DiagnosticLog.WriteFatal("videoRing",
            $"スロット {slotIndex} のペイロード解放に失敗（解放を諦めてスロットは再利用可能にする）: {error}");
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
    /// リース中のスロットを Free に戻す。破棄時に遅延解放が予約されていれば、ロック内で解放コールバックを
    /// 呼んでペイロードも解放する。リース中でなければ何もしない。
    /// </summary>
    public void ReturnLease(int slotIndex)
    {
        Exception? releaseError;
        lock (_lock)
        {
            var slot = _slots[slotIndex];
            if (slot.State != SlotState.Leased) return;

            if (!TryReleaseDeferredLocked(slot, slotIndex, out releaseError))
            {
                slot.State = SlotState.Free;
                Monitor.PulseAll(_lock);
            }
        }
        ReportReleaseFailure(slotIndex, releaseError);
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

    /// <summary>
    /// EOF 済みかつ、まだ提示していないフレーム（Writing / Ready）が残っていない（再生完了検出に使う）。
    /// リース中（Leased）は数えない。GPU 経路の vout スレッドは次のフレームが due になるまで
    /// 最後のフレームをリースしたまま再提示し続けるため、全スロットが Free になるのを待つと
    /// 「最後まで再生しても再生完了が検出されない」ことになる。
    /// </summary>
    public bool IsEofDrained
    {
        get
        {
            lock (_lock)
            {
                if (!_eofMarked) return false;
                foreach (var slot in _slots)
                    if (slot.State is SlotState.Writing or SlotState.Ready) return false;
                return true;
            }
        }
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
    /// Close 後の解放。他スレッドがまだペイロードを参照している可能性があるスロット（Leased＝読み取り中、
    /// Writing＝デコーダが変換出力中）は即座に解放せず、遅延解放を予約して参照が離れる時に解放させる。
    /// それ以外（Free / Ready）はロック内で即解放する。
    ///
    /// Leased の保護が実際に効く相手は映像を提示している側（GPU 経路の vout スレッド・CPU 経路の UI
    /// スレッド）。<see cref="ReturnLease"/> で必ず戻ってくるため、遅延解放は確実に消化される。
    ///
    /// Writing の保護は防御的な保険。呼び出し側（<c>MediaEngine.TeardownPipeline</c>）はデコードスレッドの
    /// 停止を確認できたときにしかリングを破棄しないため、通常ここに Writing は残らない。残るのは
    /// 書き込み側が Commit / Abort を通さずに離脱した場合だけで、その経路は
    /// <c>VideoDecodeThread.EmitFrame</c> の try/finally で塞いである。それでも即解放にしないのは、
    /// <see cref="Flush"/> が「Writing はデコーダが変換中のため触らない」とするのと同じ理由
    /// （sws_scale / VideoProcessorBlt の書き込み先が足元から消える）で、破棄条件が将来緩められた
    /// ときにヒープ破壊へ直行させないため。予約が消化されなければペイロードはリークするが、
    /// 生存中のスレッドの足元を解放するよりは安全側。
    /// </summary>
    public void DisposeSlots()
    {
        // 1 スロットの解放失敗で残りの解放を諦めないよう、例外はここで受けて最後にまとめて記録する
        //（記録自体はファイル I/O を伴うためロックの外で行う）
        List<(int Slot, Exception Error)>? failures = null;
        lock (_lock)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].State is SlotState.Leased or SlotState.Writing)
                {
                    _slots[i].PendingFreeOnRelease = true;
                    continue;
                }
                try
                {
                    _onFreePayload(i);
                }
                catch (Exception ex)
                {
                    (failures ??= new()).Add((i, ex));
                }
            }
        }
        if (failures != null)
            foreach (var (slot, error) in failures)
                ReportReleaseFailure(slot, error);
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

}
