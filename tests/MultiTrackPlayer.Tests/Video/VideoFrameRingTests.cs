using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Video;
using Xunit;

namespace MultiTrackPlayer.Tests.Video;

public sealed class VideoFrameRingTests : IDisposable
{
    private const int W = 8;
    private const int H = 8;
    private readonly VideoFrameRing _ring = new();

    private static readonly SeekEpoch E0 = SeekEpoch.Initial;
    private static SeekEpoch E(int value) => new(value);

    public void Dispose() => _ring.Dispose();

    /// <summary>現在の世代で 1 枚書き込んで Ready にする。</summary>
    private int CommitOne(double pts)
    {
        int slot = _ring.BeginWrite(W, H, _ring.CurrentEpoch);
        Assert.True(slot >= 0);
        _ring.CommitWrite(slot, pts);
        return slot;
    }

    /// <summary>次の世代へ Flush する（demux が採番した値を渡す動きの再現）。</summary>
    private SeekEpoch FlushToNextEpoch()
    {
        SeekEpoch next = new(_ring.CurrentEpoch.Value + 1);
        Assert.True(_ring.Flush(next), "新しい世代の Flush は必ず適用されるべき");
        return next;
    }

    [Fact]
    public void CommitWrite_DiscardsFrame_WhenFlushHappensBetweenBeginAndCommit()
    {
        // Arrange: 書き込み開始後にシーク（Flush）が発生
        int slot = _ring.BeginWrite(W, H, _ring.CurrentEpoch);
        Assert.True(slot >= 0);
        FlushToNextEpoch();

        // Act: 旧世代のコミット
        _ring.CommitWrite(slot, ptsSeconds: 1.0);

        // Assert: Ready にならず、リースできない
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out _));
    }

    [Fact]
    public void BeginWrite_ReturnsSlotFlushed_WhenFlushHappensWhileWaitingForFreeSlot()
    {
        // Arrange: 4スロット全て Ready → リース → 満杯（Free なし）
        for (int i = 0; i < 4; i++) CommitOne(pts: i);
        var leases = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, E0, out var f));
            Assert.NotNull(f);
            leases.Add(f.SlotIndex);
        }

        // Act: 別スレッドが BeginWrite でブロック → メインスレッドから Flush
        var writer = Task.Run(() => _ring.BeginWrite(W, H, E0));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200))); // ブロックしていること
        FlushToNextEpoch();

        // Assert: デッドロックせず SlotFlushed で起床する（後方シークで映像が止まる不具合の回帰テスト）
        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(VideoFrameRing.SlotFlushed, writer.Result);

        foreach (var s in leases) _ring.ReturnLease(s);
    }

    [Fact]
    public void Flush_FreesReadySlots_AndUnblocksWaitingWriter()
    {
        // Arrange: Ready 4枚で満杯（リースせず）
        for (int i = 0; i < 4; i++) CommitOne(pts: i);

        var writer = Task.Run(() => _ring.BeginWrite(W, H, E0));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));

        // Act
        FlushToNextEpoch();

        // Assert: 起床し（SlotFlushed）、Ready は全て破棄されている
        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(VideoFrameRing.SlotFlushed, writer.Result);
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out _));

        // 次の書き込みは新世代として普通に成功する
        int slot = _ring.BeginWrite(W, H, _ring.CurrentEpoch);
        Assert.True(slot >= 0);
        _ring.CommitWrite(slot, 5.0);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out var frame));
        Assert.NotNull(frame);
        Assert.Equal(5.0, frame.Pts.TotalSeconds);
    }

    [Fact(DisplayName = "TryLeaseOldest は世代が一致するフレームだけを返す")]
    public void TryLeaseOldest_ReturnsOnlyFrames_OfExactEpoch()
    {
        // Arrange: 世代0のフレームが1枚 Ready
        CommitOne(pts: 1.0);

        // Assert: 別の世代を要求すると返らない
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, E(1), out _));

        // Act: Flush（世代進行）後に新フレームを投入
        SeekEpoch after = FlushToNextEpoch();
        CommitOne(pts: 2.0);

        // Assert: 新世代のフレームだけが返る
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, after, out var frame));
        Assert.NotNull(frame);
        Assert.Equal(2.0, frame.Pts.TotalSeconds);
    }

    // demux がシークでリングを Flush した直後、映像デコードスレッドはまだ Flush 番兵に到達しておらず
    // シーク前のパケットを処理している。以前はここで書かれたフレームが新世代の刻印を得てしまい、
    // 一時停止中のシークがそれを掴んで表示したまま残っていた
    [Fact(DisplayName = "シーク前のパケットのフレームは、シーク後の世代では書き込めない")]
    public void BeginWrite_RejectsFrame_FromEpochBeforeSeek()
    {
        SeekEpoch beforeSeek = _ring.CurrentEpoch;
        FlushToNextEpoch(); // demux がシークを実行

        int slot = _ring.BeginWrite(W, H, beforeSeek);

        Assert.Equal(VideoFrameRing.SlotFlushed, slot);
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out _));
    }

    [Fact]
    public void ReturnLease_MakesSlotReusable()
    {
        // Arrange: 4枚 Ready → 1枚リース
        for (int i = 0; i < 4; i++) CommitOne(pts: i);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out var frame));
        Assert.NotNull(frame);

        // Act: 返却すると空きができ、次の BeginWrite が即座に成功する
        _ring.ReturnLease(frame.SlotIndex);
        int slot = _ring.BeginWrite(W, H, _ring.CurrentEpoch);

        // Assert
        Assert.True(slot >= 0);
        _ring.AbortWrite(slot);
    }

    [Fact]
    public void Flush_DoesNotTouchLeasedSlot()
    {
        // Arrange: 1枚リース中（UI が一時停止フレームを保持している状況）
        CommitOne(pts: 1.0);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, _ring.CurrentEpoch, out var held));
        Assert.NotNull(held);

        // Act
        FlushToNextEpoch();

        // Assert: リース中スロットは Flush の影響を受けず、返却後に再利用できる
        _ring.ReturnLease(held.SlotIndex);
        int slot = _ring.BeginWrite(W, H, _ring.CurrentEpoch);
        Assert.True(slot >= 0);
        _ring.AbortWrite(slot);
    }
}
