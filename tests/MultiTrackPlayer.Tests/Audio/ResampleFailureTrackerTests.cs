using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

public sealed class ResampleFailureTrackerTests
{
    [Fact(DisplayName = "連続失敗が閾値に達した回だけ true を返す")]
    public void RecordFailure_ReturnsTrueOnlyWhenThresholdIsReached()
    {
        var tracker = new ResampleFailureTracker(trackCount: 2, threshold: 3);

        Assert.False(tracker.RecordFailure(0));
        Assert.False(tracker.RecordFailure(0));
        Assert.True(tracker.RecordFailure(0));
        // 閾値を超えて呼び続けても再度 true にはならない
        //（呼び出し側がトラックを切り離す処理を重複実行しないため）
        Assert.False(tracker.RecordFailure(0));
        Assert.False(tracker.RecordFailure(0));
    }

    [Fact(DisplayName = "連続失敗をトラックごとに独立して数える")]
    public void RecordFailure_CountsPerTrackIndependently()
    {
        var tracker = new ResampleFailureTracker(trackCount: 2, threshold: 2);

        Assert.False(tracker.RecordFailure(0));
        // 別トラックの失敗が track0 の積み上げに影響してはならない
        Assert.False(tracker.RecordFailure(1));
        Assert.True(tracker.RecordFailure(0));
        Assert.Equal(1, tracker.GetConsecutiveFailures(1));
    }

    [Fact(DisplayName = "成功を記録すると連続失敗の数え直しが始まる")]
    public void RecordSuccess_RestartsCounting()
    {
        var tracker = new ResampleFailureTracker(trackCount: 1, threshold: 2);

        Assert.False(tracker.RecordFailure(0));
        tracker.RecordSuccess(0);

        Assert.Equal(0, tracker.GetConsecutiveFailures(0));
        // 数え直しなので、次の 1 回では閾値に達しない
        Assert.False(tracker.RecordFailure(0));
        Assert.True(tracker.RecordFailure(0));
    }

    [Fact(DisplayName = "Reset で全トラックの連続失敗が消える")]
    public void Reset_ClearsAllTracks()
    {
        var tracker = new ResampleFailureTracker(trackCount: 2, threshold: 2);
        tracker.RecordFailure(0);
        tracker.RecordFailure(1);

        tracker.Reset();

        Assert.Equal(0, tracker.GetConsecutiveFailures(0));
        Assert.Equal(0, tracker.GetConsecutiveFailures(1));
    }

    [Theory(DisplayName = "範囲外の添字は例外にせず無視する")]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeIndex_IsIgnored(int trackIndex)
    {
        var tracker = new ResampleFailureTracker(trackCount: 1, threshold: 1);

        // ここで例外を投げると音声デコードスレッドごと停止し、全トラックが無音になる
        Assert.False(tracker.RecordFailure(trackIndex));
        tracker.RecordSuccess(trackIndex);
        Assert.Equal(0, tracker.GetConsecutiveFailures(trackIndex));
    }

    [Fact(DisplayName = "音声トラックが 0 本でも生成できる")]
    public void Constructor_AcceptsZeroTracks()
    {
        var tracker = new ResampleFailureTracker(trackCount: 0);

        Assert.Equal(0, tracker.TrackCount);
        Assert.Equal(ResampleFailureTracker.DefaultThreshold, tracker.Threshold);
        Assert.False(tracker.RecordFailure(0));
    }

    [Theory(DisplayName = "不正な引数は例外にする")]
    [InlineData(-1, ResampleFailureTracker.DefaultThreshold)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public void Constructor_RejectsInvalidArguments(int trackCount, int threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResampleFailureTracker(trackCount, threshold));
    }
}
