using MultiTrackPlayer.Engine.Video;

namespace MultiTrackPlayer.Tests.Video;

public sealed class FrameSelectorTests
{
    private const double FrameDuration = 0.02; // 50fps 相当（値自体の意味は無く判定ロジック検証用）

    [Fact]
    public void SelectDue_ReturnsNone_WhenNoCandidates()
    {
        var result = FrameSelector.SelectDue(Array.Empty<CandidateFrame>(), clockPositionSeconds: 1.0, FrameDuration);
        Assert.Null(result.SlotIndex);
    }

    [Fact]
    public void SelectDue_ReturnsNone_WhenAllCandidatesAreAheadOfClock()
    {
        var candidates = new[] { new CandidateFrame(0, 1.02) }; // dueThreshold = 1.01 より先行
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);
        Assert.Null(result.SlotIndex);
    }

    [Fact]
    public void SelectDue_SelectsSingleDueFrame_WithZeroDropped()
    {
        var candidates = new[] { new CandidateFrame(3, 0.99) };
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);

        Assert.Equal(3, result.SlotIndex);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void SelectDue_SelectsLatestDueFrame_AndCountsOlderOnesAsDropped()
    {
        var candidates = new[]
        {
            new CandidateFrame(0, 0.90),
            new CandidateFrame(1, 0.95),
            new CandidateFrame(2, 1.00),
        };
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);

        Assert.Equal(2, result.SlotIndex);
        Assert.Equal(2, result.DroppedCount);
    }

    [Fact]
    public void SelectDue_MixedCandidates_StopsAtFirstFrameAheadOfClock()
    {
        var candidates = new[]
        {
            new CandidateFrame(0, 0.95), // due
            new CandidateFrame(1, 1.00), // due (dueThreshold=1.01)
            new CandidateFrame(2, 1.05), // 先行、選ばれない
        };
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);

        Assert.Equal(1, result.SlotIndex);
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void SelectDue_GraceWindow_IncludesFrameWithinHalfFrameDurationAhead()
    {
        // dueThreshold = 1.0 + 0.02/2 = 1.01。1.005 は猶予内で due 扱い。
        var candidates = new[] { new CandidateFrame(7, 1.005) };
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);

        Assert.Equal(7, result.SlotIndex);
    }

    [Fact]
    public void SelectDue_GraceWindow_ExcludesFrameClearlyBeyondHalfFrameDuration()
    {
        var candidates = new[] { new CandidateFrame(7, 1.03) }; // dueThreshold=1.01 を明確に超える
        var result = FrameSelector.SelectDue(candidates, clockPositionSeconds: 1.0, FrameDuration);

        Assert.Null(result.SlotIndex);
    }
}
