using MultiTrackPlayer.Engine.Video;
using Xunit;

namespace MultiTrackPlayer.Tests.Video;

public sealed class VideoFrameRingTests : IDisposable
{
    private const int W = 8;
    private const int H = 8;
    private readonly VideoFrameRing _ring = new();

    public void Dispose() => _ring.Dispose();

    private int CommitOne(double pts)
    {
        int slot = _ring.BeginWrite(W, H);
        Assert.True(slot >= 0);
        _ring.CommitWrite(slot, pts);
        return slot;
    }

    [Fact]
    public void CommitWrite_DiscardsFrame_WhenFlushHappensBetweenBeginAndCommit()
    {
        // Arrange: 書き込み開始後にシーク（Flush）が発生
        int slot = _ring.BeginWrite(W, H);
        Assert.True(slot >= 0);
        _ring.Flush();

        // Act: 旧世代のコミット
        _ring.CommitWrite(slot, ptsSeconds: 1.0);

        // Assert: Ready にならず、リースできない
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, minSerial: 0, out _));
    }

    [Fact]
    public void BeginWrite_ReturnsSlotFlushed_WhenFlushHappensWhileWaitingForFreeSlot()
    {
        // Arrange: 4スロット全て Ready → リース → 満杯（Free なし）
        for (int i = 0; i < 4; i++) CommitOne(pts: i);
        var leases = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, 0, out var f));
            leases.Add(f.SlotIndex);
        }

        // Act: 別スレッドが BeginWrite でブロック → メインスレッドから Flush
        var writer = Task.Run(() => _ring.BeginWrite(W, H));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200))); // ブロックしていること
        _ring.Flush();

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

        var writer = Task.Run(() => _ring.BeginWrite(W, H));
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(200)));

        // Act
        _ring.Flush();

        // Assert: 起床し（SlotFlushed）、Ready は全て破棄されている
        Assert.True(writer.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(VideoFrameRing.SlotFlushed, writer.Result);
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, 0, out _));

        // 次の書き込みは新世代として普通に成功する
        int slot = _ring.BeginWrite(W, H);
        Assert.True(slot >= 0);
        _ring.CommitWrite(slot, 5.0);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, 0, out var frame));
        Assert.Equal(5.0, frame.PtsSeconds);
    }

    [Fact]
    public void TryLeaseOldest_SkipsFrames_BelowMinSerial()
    {
        // Arrange: 世代0のフレームが1枚 Ready
        CommitOne(pts: 1.0);
        int nextSerial = _ring.CurrentSerial + 1;

        // Assert: 新世代を要求すると旧世代は返らない
        Assert.False(_ring.TryLeaseOldest(TimeSpan.Zero, minSerial: nextSerial, out _));

        // Act: Flush（世代進行）後に新フレームを投入
        _ring.Flush();
        CommitOne(pts: 2.0);

        // Assert: 新世代のフレームだけが返る
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, minSerial: nextSerial, out var frame));
        Assert.Equal(2.0, frame.PtsSeconds);
    }

    [Fact]
    public void ReturnLease_MakesSlotReusable()
    {
        // Arrange: 4枚 Ready → 1枚リース
        for (int i = 0; i < 4; i++) CommitOne(pts: i);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, 0, out var frame));

        // Act: 返却すると空きができ、次の BeginWrite が即座に成功する
        _ring.ReturnLease(frame.SlotIndex);
        int slot = _ring.BeginWrite(W, H);

        // Assert
        Assert.True(slot >= 0);
        _ring.AbortWrite(slot);
    }

    [Fact]
    public void Flush_DoesNotTouchLeasedSlot()
    {
        // Arrange: 1枚リース中（UI が一時停止フレームを保持している状況）
        CommitOne(pts: 1.0);
        Assert.True(_ring.TryLeaseOldest(TimeSpan.Zero, 0, out var held));

        // Act
        _ring.Flush();

        // Assert: リース中スロットは Flush の影響を受けず、返却後に再利用できる
        _ring.ReturnLease(held.SlotIndex);
        int slot = _ring.BeginWrite(W, H);
        Assert.True(slot >= 0);
        _ring.AbortWrite(slot);
    }
}
