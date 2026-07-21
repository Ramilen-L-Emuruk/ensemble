namespace MultiTrackPlayer.Engine.Thumbnails;

/// <summary>
/// サムネイル生成のサンプリング間隔・グリッド列数を決める純粋ロジック。
/// 基本は1秒間隔だが、長尺ファイルでは合計サンプル数が MaxSamples を超えないよう間隔を自動で広げる。
/// </summary>
public static class ThumbnailPlan
{
    public const int MaxSamples = 1500;
    public const double MinIntervalSeconds = 1.0;

    public static double ComputeInterval(double durationSeconds)
        => durationSeconds <= 0
            ? MinIntervalSeconds
            : Math.Max(MinIntervalSeconds, durationSeconds / MaxSamples);

    public static IReadOnlyList<double> ComputeSampleTimes(double durationSeconds)
    {
        if (durationSeconds <= 0) return Array.Empty<double>();

        double interval = ComputeInterval(durationSeconds);
        int count = (int)Math.Ceiling(durationSeconds / interval);
        var times = new double[count];
        for (int i = 0; i < count; i++)
            times[i] = i * interval;
        return times;
    }

    public static int ComputeColumns(int count)
        => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))));
}
