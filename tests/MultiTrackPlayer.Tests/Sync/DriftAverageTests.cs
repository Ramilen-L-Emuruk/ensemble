using MultiTrackPlayer.Engine.Sync;

namespace MultiTrackPlayer.Tests.Sync;

public sealed class DriftAverageTests
{
    [Fact]
    public void Get_ReturnsZero_WhenNoSamplesAdded()
    {
        var avg = new DriftAverage();
        Assert.Equal(0.0, avg.Get());
    }

    [Fact]
    public void Get_ReturnsSingleValue_AfterOneSample()
    {
        var avg = new DriftAverage(range: 10);
        avg.Update(0.05);
        Assert.Equal(0.05, avg.Get(), precision: 10);
    }

    [Fact]
    public void Get_ReturnsMovingAverage_OverRange()
    {
        var avg = new DriftAverage(range: 3);
        avg.Update(0.0);  // weight=1: 0
        avg.Update(0.3);  // weight=2: (0*1 + 0.3) / 2 = 0.15
        avg.Update(0.6);  // weight=3: (0.15*2 + 0.6) / 3 = 0.3
        Assert.Equal(0.3, avg.Get(), precision: 10);
    }

    [Fact]
    public void Get_CapsWeight_AtRange()
    {
        var avg = new DriftAverage(range: 2);
        avg.Update(1.0);  // weight=1: 1.0
        avg.Update(0.0);  // weight=2: 0.5
        avg.Update(0.0);  // weight=2 capped: (0.5*1 + 0) / 2 = 0.25
        Assert.Equal(0.25, avg.Get(), precision: 10);
    }

    [Fact]
    public void Reset_ClearsValueAndCount()
    {
        var avg = new DriftAverage(range: 10);
        avg.Update(1.0);
        avg.Update(0.5);
        avg.Reset();

        Assert.Equal(0.0, avg.Get());

        avg.Update(0.8);
        Assert.Equal(0.8, avg.Get(), precision: 10);
    }

    [Fact]
    public void Get_Converges_WithRange10()
    {
        var avg = new DriftAverage(range: 10);
        for (int i = 0; i < 20; i++)
            avg.Update(0.1);
        Assert.Equal(0.1, avg.Get(), precision: 5);
    }
}
