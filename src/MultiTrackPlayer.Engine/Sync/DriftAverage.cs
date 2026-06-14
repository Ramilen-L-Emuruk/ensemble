namespace MultiTrackPlayer.Engine.Sync;

// VLC src/clock/clock_internal.c の average_t / AvgUpdate() を C# 移植
// range サンプルの加重移動平均でドリフト値を平滑化する
internal sealed class DriftAverage
{
    private readonly int _range;
    private double _value;
    private int _count;

    public DriftAverage(int range = 10) => _range = range;

    public void Update(double sample)
    {
        _count++;
        int weight = Math.Min(_count, _range);
        _value = (_value * (weight - 1) + sample) / weight;
    }

    public void Reset()
    {
        _value = 0;
        _count = 0;
    }

    public double Get() => _value;
}
