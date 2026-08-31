using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Video;
using Xunit;

namespace MultiTrackPlayer.Tests.Video;

/// <summary>
/// <see cref="SlotSequencer"/> の状態遷移を、ペイロード（バッファ）非依存の形で検証する。
/// VideoFrameRingTests が守っていた「後方シークで映像が止まる」系の回帰を、状態機械単体でも押さえる。
/// </summary>
public sealed class SlotSequencerTests
{
    private static readonly SeekEpoch E0 = SeekEpoch.Initial;
    private static SeekEpoch E(int value) => new(value);

    /// <summary>現在の世代でスロットを確保する（通常のデコード動作）。</summary>
    private static int Acquire(SlotSequencer seq) => seq.BeginWrite(seq.CurrentEpoch, _ => { });

    /// <summary>次の世代へ Flush する（demux が採番した値を渡す動きの再現）。</summary>
    private static SeekEpoch FlushToNextEpoch(SlotSequencer seq)
    {
        SeekEpoch next = new(seq.CurrentEpoch.Value + 1);
        Assert.True(seq.Flush(next), "新しい世代の Flush は必ず適用されるべき");
        return next;
    }

    [Fact(DisplayName = "EOF 前は提示待ちが無くてもドレイン済みとは見なさない")]
    public void IsEofDrained_IsFalse_BeforeMarkEof()
    {
        var seq = new SlotSequencer(4, _ => { });

        Assert.False(seq.IsEofDrained);
    }

    [Fact(DisplayName = "EOF 後に提示待ちのフレームが残っていればドレイン済みにならない")]
    public void IsEofDrained_IsFalse_WhileReadyFrameRemains()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 1.0);
        seq.MarkEof();

        Assert.False(seq.IsEofDrained);
    }

    // GPU 経路の vout スレッドは、次のフレームが due になるまで最後のフレームをリースしたまま
    // 再提示し続ける。リース中を「未ドレイン」と見なすと、最後まで再生しても再生完了が
    // 永久に検出されない（実機で「尺を超えて位置が伸び続ける」として現れた）
    [Fact(DisplayName = "EOF 後にリース中のフレームだけが残っていればドレイン済みと見なす")]
    public void IsEofDrained_IsTrue_WhenOnlyLeasedFrameRemains()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 1.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out _, out _));
        seq.MarkEof();

        Assert.True(seq.IsEofDrained);
    }

    [Fact(DisplayName = "EOF 後に書き込み中のフレームが残っていればドレイン済みにならない")]
    public void IsEofDrained_IsFalse_WhileWritingFrameRemains()
    {
        var seq = new SlotSequencer(4, _ => { });
        Acquire(seq); // Commit せず Writing のまま
        seq.MarkEof();

        Assert.False(seq.IsEofDrained);
    }

    [Fact]
    public void CommitWrite_DiscardsFrame_WhenFlushHappensBetweenBeginAndCommit()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        Assert.True(slot >= 0);
        FlushToNextEpoch(seq);

        seq.CommitWrite(slot, 1.0);

        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out _, out _));
    }

    [Fact]
    public void BeginWrite_ReturnsSlotFlushed_WhenFlushHappensWhileWaitingForFreeSlot()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }
        var leases = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, E0, out int s, out _));
            leases.Add(s);
        }

        var writer = Task.Run(() => seq.BeginWrite(E0, _ => { }));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));
        FlushToNextEpoch(seq);

        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(SlotSequencer.SlotFlushed, writer.Result);

        foreach (var s in leases) seq.ReturnLease(s);
    }

    [Fact]
    public void Flush_FreesReadySlots_AndUnblocksWaitingWriter()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }

        var writer = Task.Run(() => seq.BeginWrite(E0, _ => { }));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));

        FlushToNextEpoch(seq);

        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(SlotSequencer.SlotFlushed, writer.Result);
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out _, out _));

        int slot = Acquire(seq);
        Assert.True(slot >= 0);
        seq.CommitWrite(slot, 5.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out _, out double pts));
        Assert.Equal(5.0, pts);
    }

    // demux はシーク実行時にリングを Flush するが、デコードスレッドはまだ Flush 番兵に到達しておらず
    // シーク前のパケットを処理し続けている。Flush で全スロットが空いた直後なので、以前の実装では
    // BeginWrite が即座に成功し、シーク前のフレームが新世代の刻印を得て提示対象になっていた
    // （一時停止中のシークでシーク前のフレームが表示されたまま残る不具合）。
    // データ側の世代を渡すことで、空きスロットがあっても弾かれる
    [Fact(DisplayName = "シーク前のパケットから作られたフレームは、空きスロットがあっても書き込めない")]
    public void BeginWrite_ReturnsSlotFlushed_WhenFrameBelongsToOlderEpoch()
    {
        int acquiredCount = 0;
        var seq = new SlotSequencer(4, _ => { });
        Assert.True(seq.Flush(E(1))); // demux がシークを実行した瞬間（全スロットは Free）

        int result = seq.BeginWrite(E0, _ => acquiredCount++);

        Assert.Equal(SlotSequencer.SlotFlushed, result);
        // ペイロード確保のコールバックも呼ばれない（4K テクスチャ生成を丸ごと省ける）
        Assert.Equal(0, acquiredCount);
    }

    [Fact(DisplayName = "未来の世代を指定した書き込みも弾く")]
    public void BeginWrite_ReturnsSlotFlushed_WhenFrameBelongsToNewerEpoch()
    {
        var seq = new SlotSequencer(4, _ => { });

        Assert.Equal(SlotSequencer.SlotFlushed, seq.BeginWrite(E(1), _ => { }));
    }

    // 以前は「指定した世代以上なら通す」下限比較だったため、シーク前のパケットから作られたフレーム
    // （新世代の刻印を持ってしまったもの）を掴んでいた。等値にすることで取り違えを構造的に防ぐ
    [Fact(DisplayName = "TryLeaseOldest は世代が一致するフレームだけを返す")]
    public void TryLeaseOldest_ReturnsOnlyFrames_OfExactEpoch()
    {
        var seq = new SlotSequencer(4, _ => { });
        int s = Acquire(seq);
        seq.CommitWrite(s, 1.0);

        // 別の世代を指定したら、古い方も新しい方も対象外
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, E(1), out _, out _));
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, E(5), out _, out _));

        // 一致する世代なら返る
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, E0, out _, out double pts));
        Assert.Equal(1.0, pts);
    }

    [Fact(DisplayName = "シーク後は新しい世代のフレームだけが取り出せる")]
    public void TryLeaseOldest_ReturnsNewEpochFrame_AfterFlush()
    {
        var seq = new SlotSequencer(4, _ => { });
        int s = Acquire(seq);
        seq.CommitWrite(s, 1.0);

        SeekEpoch after = FlushToNextEpoch(seq);
        int s2 = Acquire(seq);
        seq.CommitWrite(s2, 2.0);

        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, after, out _, out double pts));
        Assert.Equal(2.0, pts);
        // シーク前の世代を指定しても、Flush で Free に戻されているので何も返らない
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, E0, out _, out _));
    }

    // 以前は demux とデコードスレッドの 2 箇所がリングを Flush しており、1 シークで世代が 2 進んでいた。
    // 世代を外から受け取る形にしたうえで「同じか古い世代の Flush は無視」とすることで、
    // 二重の掃除で新世代の正しいフレームを捨てる事故を型と規約の両面で防ぐ
    [Fact(DisplayName = "同じ世代での再 Flush は新世代のフレームを捨てない")]
    public void Flush_IgnoresSameEpoch_AndKeepsAlreadyWrittenFrames()
    {
        var seq = new SlotSequencer(4, _ => { });
        Assert.True(seq.Flush(E(1)));
        int s = Acquire(seq);
        seq.CommitWrite(s, 3.0);

        // 呼び出し規約違反（同じ世代での二度目）。適用せず false を返すこと
        Assert.False(seq.Flush(E(1)));

        Assert.Equal(E(1), seq.CurrentEpoch);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, E(1), out _, out double pts));
        Assert.Equal(3.0, pts);
    }

    [Fact(DisplayName = "古い世代での Flush は世代を巻き戻さない")]
    public void Flush_IgnoresOlderEpoch()
    {
        var seq = new SlotSequencer(4, _ => { });
        Assert.True(seq.Flush(E(5)));

        Assert.False(seq.Flush(E(2)));

        Assert.Equal(E(5), seq.CurrentEpoch);
    }

    // シーク要求はコアレスされるため、採番されたまま使われない世代が飛び番として残る。
    // リング側は連番を前提にせず、渡された値をそのまま採用すること
    [Fact(DisplayName = "飛び番の世代でも Flush できる")]
    public void Flush_AcceptsNonContiguousEpoch()
    {
        var seq = new SlotSequencer(4, _ => { });

        Assert.True(seq.Flush(E(9)));

        Assert.Equal(E(9), seq.CurrentEpoch);
        int s = Acquire(seq);
        Assert.True(s >= 0);
        seq.CommitWrite(s, 1.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, E(9), out _, out _));
    }

    [Fact]
    public void ReturnLease_MakesSlotReusable()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out int leased, out _));

        seq.ReturnLease(leased);
        int slot = Acquire(seq);

        Assert.True(slot >= 0);
        seq.AbortWrite(slot);
    }

    [Fact]
    public void Flush_DoesNotTouchLeasedSlot()
    {
        var seq = new SlotSequencer(4, _ => { });
        int s = Acquire(seq);
        seq.CommitWrite(s, 1.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out int held, out _));

        FlushToNextEpoch(seq);

        seq.ReturnLease(held);
        int slot = Acquire(seq);
        Assert.True(slot >= 0);
        seq.AbortWrite(slot);
    }

    [Fact(DisplayName = "破棄時、リース中のスロットは即解放せずリース返却時に解放する")]
    public void DisposeSlots_DefersFreeForLeasedSlot_AndFreesOnReturn()
    {
        var freed = new List<int>();
        var seq = new SlotSequencer(4, freed.Add);
        int s = Acquire(seq);
        seq.CommitWrite(s, 1.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out int held, out _));

        seq.Close();
        seq.DisposeSlots();

        // Leased スロットは即解放されず、残り3スロットだけが解放される
        Assert.DoesNotContain(held, freed);
        Assert.Equal(3, freed.Count);

        // リース返却時に遅延解放される
        seq.ReturnLease(held);
        Assert.Contains(held, freed);
    }

    // スレッド停止待ちがタイムアウトすると、デコードスレッドが Writing スロットへ書き込んでいる最中に
    // リングが破棄される。そこでペイロードを解放すると sws_scale / VideoProcessorBlt の
    // 書き込み先が足元から消えてネイティブヒープが壊れる（過去に実クラッシュを出した経路）
    [Fact(DisplayName = "破棄時、書き込み中のスロットは即解放しない")]
    public void DisposeSlots_DefersFreeForWritingSlot()
    {
        var freed = new List<int>();
        var seq = new SlotSequencer(4, freed.Add);
        int writing = Acquire(seq); // Commit せず Writing のまま

        seq.Close();
        seq.DisposeSlots();

        Assert.DoesNotContain(writing, freed);
        Assert.Equal(3, freed.Count);
    }

    [Fact(DisplayName = "書き込み中に予約された遅延解放は CommitWrite で解放される")]
    public void CommitWrite_FreesDeferredPayload_AfterDisposeSlots()
    {
        var freed = new List<int>();
        var seq = new SlotSequencer(4, freed.Add);
        int writing = Acquire(seq);

        seq.Close();
        seq.DisposeSlots();
        Assert.DoesNotContain(writing, freed);

        // 生き残っていたデコードスレッドが変換を終えて戻ってきた場面
        seq.CommitWrite(writing, 1.0);

        Assert.Contains(writing, freed);
        // 破棄後に Ready へ昇格させてはいけない（提示側に拾わせない）
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, seq.CurrentEpoch, out _, out _));
    }

    [Fact(DisplayName = "書き込み中に予約された遅延解放は AbortWrite でも解放される")]
    public void AbortWrite_FreesDeferredPayload_AfterDisposeSlots()
    {
        var freed = new List<int>();
        var seq = new SlotSequencer(4, freed.Add);
        int writing = Acquire(seq);

        seq.Close();
        seq.DisposeSlots();
        seq.AbortWrite(writing);

        Assert.Contains(writing, freed);
    }

    [Fact(DisplayName = "遅延解放は 1 度だけ行われる")]
    public void DeferredFree_HappensOnlyOnce()
    {
        var freed = new List<int>();
        var seq = new SlotSequencer(4, freed.Add);
        int writing = Acquire(seq);

        seq.Close();
        seq.DisposeSlots();
        seq.AbortWrite(writing);
        seq.AbortWrite(writing);

        Assert.Equal(1, freed.Count(i => i == writing));
    }

    // ── IsWaitingForFrameTime（提示が止まっているのが正常かどうかの判断に使う）──
    //
    // 「Ready はあるが、どれもまだ due でない」＝次のフレームの時刻を待っているだけで健全。
    // 低フレームレート・VFR の動画では数秒それが続くため、これを見ないと健全な再生を
    // 滞留と誤判定する。フレーム間隔は 1 秒（due 判定の許容は ±0.5 秒）として書いてある。

    private const double FrameDuration = 1.0;

    [Fact(DisplayName = "Ready が無ければ時刻待ちではない（供給が止まっている）")]
    public void IsWaitingForFrameTime_IsFalse_WhenNoReadyFrame()
    {
        var seq = new SlotSequencer(4, _ => { });

        Assert.False(seq.IsWaitingForFrameTime(clockPositionSeconds: 0.0, FrameDuration));
    }

    [Fact(DisplayName = "Ready がまだ先の時刻なら時刻待ちと見なす")]
    public void IsWaitingForFrameTime_IsTrue_WhenReadyFrameIsNotDueYet()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 10.0);

        Assert.True(seq.IsWaitingForFrameTime(clockPositionSeconds: 0.0, FrameDuration));
    }

    [Fact(DisplayName = "Ready が due なら時刻待ちではない（出せるのに出ていない）")]
    public void IsWaitingForFrameTime_IsFalse_WhenReadyFrameIsDue()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 1.0);

        Assert.False(seq.IsWaitingForFrameTime(clockPositionSeconds: 5.0, FrameDuration));
    }

    [Fact(DisplayName = "due と未 due が混在していれば時刻待ちではない")]
    public void IsWaitingForFrameTime_IsFalse_WhenAnyFrameIsDue()
    {
        var seq = new SlotSequencer(4, _ => { });
        int due = Acquire(seq);
        seq.CommitWrite(due, 1.0);
        int future = Acquire(seq);
        seq.CommitWrite(future, 10.0);

        Assert.False(seq.IsWaitingForFrameTime(clockPositionSeconds: 2.0, FrameDuration));
    }

    [Fact(DisplayName = "due の判定は TryLeaseDue と同じ境界で行われる")]
    public void IsWaitingForFrameTime_UsesSameDueBoundaryAsTryLeaseDue()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 10.0);
        // due 判定は pts <= clock + frameDuration/2。clock=9.5 で境界にちょうど乗る
        const double clockAtBoundary = 9.5;

        bool waiting = seq.IsWaitingForFrameTime(clockAtBoundary, FrameDuration);
        bool leased = seq.TryLeaseDue(clockAtBoundary, FrameDuration, out _, out _, out _);

        Assert.False(waiting);
        Assert.True(leased);
    }

    [Fact(DisplayName = "リースしたフレームは時刻待ちの判定に数えない")]
    public void IsWaitingForFrameTime_IgnoresLeasedFrame()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 1.0);
        Assert.True(seq.TryLeaseDue(2.0, FrameDuration, out _, out _, out _));

        // 提示中のフレームは「これから出すもの」ではないので、時刻待ちの根拠にはならない
        Assert.False(seq.IsWaitingForFrameTime(clockPositionSeconds: 2.0, FrameDuration));
    }

    [Fact(DisplayName = "Flush で Ready が捨てられれば時刻待ちではなくなる")]
    public void IsWaitingForFrameTime_IsFalse_AfterFlushDiscardsReady()
    {
        var seq = new SlotSequencer(4, _ => { });
        int slot = Acquire(seq);
        seq.CommitWrite(slot, 10.0);
        Assert.True(seq.IsWaitingForFrameTime(0.0, FrameDuration));

        FlushToNextEpoch(seq);

        Assert.False(seq.IsWaitingForFrameTime(0.0, FrameDuration));
    }
}
