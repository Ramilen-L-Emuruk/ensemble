using MultiTrackPlayer.Engine.Video;
using Xunit;

namespace MultiTrackPlayer.Tests.Video;

/// <summary>
/// <see cref="SlotSequencer"/> の状態遷移を、ペイロード（バッファ）非依存の形で検証する。
/// VideoFrameRingTests が守っていた「後方シークで映像が止まる」系の回帰を、状態機械単体でも押さえる。
/// </summary>
public sealed class SlotSequencerTests
{
    private static int Acquire(SlotSequencer seq) => seq.BeginWrite(_ => { });

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
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, minSerial: 0, out _, out _));
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
        seq.Flush();

        seq.CommitWrite(slot, 1.0);

        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, minSerial: 0, out _, out _));
    }

    [Fact]
    public void BeginWrite_ReturnsSlotFlushed_WhenFlushHappensWhileWaitingForFreeSlot()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }
        var leases = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, 0, out int s, out _));
            leases.Add(s);
        }

        var writer = Task.Run(() => seq.BeginWrite(_ => { }));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));
        seq.Flush();

        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(SlotSequencer.SlotFlushed, writer.Result);

        foreach (var s in leases) seq.ReturnLease(s);
    }

    [Fact]
    public void Flush_FreesReadySlots_AndUnblocksWaitingWriter()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }

        var writer = Task.Run(() => seq.BeginWrite(_ => { }));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));

        seq.Flush();

        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(SlotSequencer.SlotFlushed, writer.Result);
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, 0, out _, out _));

        int slot = Acquire(seq);
        Assert.True(slot >= 0);
        seq.CommitWrite(slot, 5.0);
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, 0, out _, out double pts));
        Assert.Equal(5.0, pts);
    }

    [Fact]
    public void TryLeaseOldest_SkipsFrames_BelowMinSerial()
    {
        var seq = new SlotSequencer(4, _ => { });
        int s = Acquire(seq);
        seq.CommitWrite(s, 1.0);
        int nextSerial = seq.CurrentSerial + 1;

        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, minSerial: nextSerial, out _, out _));

        seq.Flush();
        int s2 = Acquire(seq);
        seq.CommitWrite(s2, 2.0);

        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, minSerial: nextSerial, out _, out double pts));
        Assert.Equal(2.0, pts);
    }

    [Fact]
    public void ReturnLease_MakesSlotReusable()
    {
        var seq = new SlotSequencer(4, _ => { });
        for (int i = 0; i < 4; i++) { int s = Acquire(seq); seq.CommitWrite(s, i); }
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, 0, out int leased, out _));

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
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, 0, out int held, out _));

        seq.Flush();

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
        Assert.True(seq.TryLeaseOldest(TimeSpan.Zero, 0, out int held, out _));

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
        Assert.False(seq.TryLeaseOldest(TimeSpan.Zero, minSerial: 0, out _, out _));
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
}
