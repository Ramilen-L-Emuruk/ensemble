using MultiTrackPlayer.Engine.Thumbnails;

namespace MultiTrackPlayer.Tests.Thumbnails;

public sealed class ThumbnailPlanTests
{
    [Fact]
    public void ComputeInterval_ReturnsOneSecond_ForShortVideo()
    {
        // 6分(360秒)動画: 360/1500 = 0.24 < 下限なので1秒間隔のまま
        double interval = ThumbnailPlan.ComputeInterval(360.0);

        Assert.Equal(1.0, interval);
    }

    [Fact]
    public void ComputeInterval_WidensInterval_ToStayUnderMaxSamples()
    {
        // 3時間(10800秒)動画: 10800/1500 = 7.2秒間隔に自動で広がる
        double interval = ThumbnailPlan.ComputeInterval(10800.0);

        Assert.Equal(7.2, interval, precision: 6);
    }

    [Fact]
    public void ComputeInterval_ReturnsMinInterval_WhenDurationIsZeroOrNegative()
    {
        Assert.Equal(ThumbnailPlan.MinIntervalSeconds, ThumbnailPlan.ComputeInterval(0.0));
        Assert.Equal(ThumbnailPlan.MinIntervalSeconds, ThumbnailPlan.ComputeInterval(-5.0));
    }

    [Fact]
    public void ComputeSampleTimes_ReturnsEmpty_WhenDurationIsZeroOrNegative()
    {
        Assert.Empty(ThumbnailPlan.ComputeSampleTimes(0.0));
        Assert.Empty(ThumbnailPlan.ComputeSampleTimes(-1.0));
    }

    [Fact]
    public void ComputeSampleTimes_ReturnsOneSampleForEachSecond_ForSixMinuteVideo()
    {
        var times = ThumbnailPlan.ComputeSampleTimes(360.0);

        Assert.Equal(360, times.Count);
        Assert.Equal(0.0, times[0]);
        Assert.Equal(359.0, times[^1]);
    }

    [Fact]
    public void ComputeSampleTimes_NeverExceedsMaxSamples_ForLongVideo()
    {
        var times = ThumbnailPlan.ComputeSampleTimes(10800.0);

        Assert.True(times.Count <= ThumbnailPlan.MaxSamples);
        Assert.Equal(ThumbnailPlan.MaxSamples, times.Count);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(72, 9)]
    [InlineData(360, 19)]
    public void ComputeColumns_ReturnsRoughlySquareGrid(int count, int expectedColumns)
    {
        int columns = ThumbnailPlan.ComputeColumns(count);

        Assert.Equal(expectedColumns, columns);
    }
}
