using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Diagnostics;

/// <summary>
/// 最後にリースしたフレームと再生クロックのずれの記録。時刻・クロックは呼び出し側が渡す設計なので、
/// 実時間を待たずに検証できる。
/// </summary>
public class VideoLagMonitorTests
{
    private const long Interval = VideoLagMonitor.ReportIntervalMs;
    // 評価の間隔（状態通知の周期と同じ 100ms）。「開始」は評価 2 回続けて超えたとき
    private const long Persist = 100;
    private const double Fps60 = 1.0 / 60.0;

    private static string? Measure(VideoLagMonitor monitor, long now, double clock, double frameDuration = Fps60) =>
        monitor.Evaluate(now, clock, frameDuration, LagObservation.Measurable, null);

    private static string? NotYetDue(VideoLagMonitor monitor, long now, double clock) =>
        monitor.Evaluate(now, clock, Fps60, LagObservation.NextFrameNotYetDue, null);

    private static string? Unmeasurable(VideoLagMonitor monitor, long now, double clock, string reason) =>
        monitor.Evaluate(now, clock, Fps60, LagObservation.NotMeasurable, reason);

    /// <summary>ずれを 2 周続けて「開始」まで進め、その行を返す（1 周目は持続の条件で記録しない）。</summary>
    private static string StartLagging(VideoLagMonitor monitor, long firstTick, double clock)
    {
        Assert.Null(Measure(monitor, firstTick, clock));
        string? line = Measure(monitor, firstTick + Persist, clock);
        Assert.NotNull(line);
        return line;
    }

    [Fact(DisplayName = "ずれが閾値以内なら何も記録しない")]
    public void Evaluate_WithinThreshold_ReturnsNull()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.00);

        Assert.Null(Measure(monitor, 0, 10.05));
        Assert.Null(Measure(monitor, 100, 9.95));
        Assert.Null(Measure(monitor, 200, 10.05));
    }

    [Fact(DisplayName = "まだ 1 枚も提示していなければ何も記録しない")]
    public void Evaluate_NothingPresented_ReturnsNull()
    {
        var monitor = new VideoLagMonitor();

        Assert.Null(Measure(monitor, 0, 10.0));
        Assert.Null(Measure(monitor, 100, 10.0));
    }

    [Fact(DisplayName = "測れない状態（一時停止・シーク後の待ち）では大きくずれていても記録しない")]
    public void Evaluate_NotMeasurable_ReturnsNull()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);

        Assert.Null(Unmeasurable(monitor, 0, 15.0, "再生中でない"));
        Assert.Null(Unmeasurable(monitor, 100, 15.0, "再生中でない"));
    }

    [Fact(DisplayName = "1 周だけのずれは記録しない（シーク直後の古いフレーム・リセットとの入れ違い）")]
    public void Evaluate_SingleTickExcursion_IsNotReported()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(248.633);

        Assert.Null(Measure(monitor, 0, 245.603));
        monitor.NotePresented(245.600);
        Assert.Null(Measure(monitor, Persist, 245.603));
    }

    [Fact(DisplayName = "映像の遅れが 2 周続いたら開始を記録し、超え始めた時点からの長さと負のずれを載せる")]
    public void Evaluate_VideoBehindPersists_ReportsStartWithNegativeLag()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);

        string line = StartLagging(monitor, 0, 10.5);

        Assert.Contains($"開始（最初に超えた評価から{Persist}ms）", line);
        Assert.Contains("lagMs=-500", line);
    }

    [Fact(DisplayName = "映像の先行が許容を超えて続いたら正のずれとして記録する")]
    public void Evaluate_VideoAheadPersists_ReportsPositiveLag()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(12.0);

        string line = StartLagging(monitor, 0, 11.7);

        Assert.Contains("lagMs=300", line);
    }

    [Fact(DisplayName = "映像の先行は、次のフレームの時刻待ちと重なっても免除しない")]
    public void Evaluate_AheadWhileNextFrameNotYetDue_IsStillReported()
    {
        // 映像が先行していれば次のフレームは必ず時刻待ちになる。免除すると先走りが見えなくなる
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(12.0);

        Assert.Null(NotYetDue(monitor, 0, 11.7));
        string? line = NotYetDue(monitor, Persist, 11.7);

        Assert.NotNull(line);
        Assert.Contains("開始", line);
        Assert.Contains("lagMs=300", line);
    }

    [Fact(DisplayName = "映像の遅れは、次のフレームの時刻待ちなら免除する")]
    public void Evaluate_BehindWhileNextFrameNotYetDue_IsNotLag()
    {
        // 低フレームレート・VFR ではフレーム間隔が閾値を超える。リングに次のフレームがあって
        // まだ時刻になっていないなら、映像は時刻どおりに出ている
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);

        Assert.Null(NotYetDue(monitor, 0, 10.4));
        Assert.Null(NotYetDue(monitor, Persist, 10.4));
    }

    [Fact(DisplayName = "低フレームレートでは、半フレーム以内の先行をずれとしない")]
    public void Evaluate_LowFrameRateAheadWithinHalfFrame_IsNotLag()
    {
        // 2fps ならリースは表示時刻がクロック＋0.25 秒以内のフレームを選ぶ。0.2 秒の先行は健全
        const double Fps2 = 0.5;
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.2);

        Assert.Null(Measure(monitor, 0, 10.0, Fps2));
        Assert.Null(Measure(monitor, Persist, 10.0, Fps2));
        Assert.Equal(0.3, VideoLagMonitor.AheadToleranceSeconds(Fps2), 6);
        Assert.Equal(VideoLagMonitor.ThresholdSeconds, VideoLagMonitor.AheadToleranceSeconds(Fps60), 6);
    }

    [Fact(DisplayName = "映像が止まったままクロックが進むと、ずれが広がって記録される")]
    public void Evaluate_FrozenVideo_LagGrowsWithClock()
    {
        // PlaybackStatistics.VideoLagSec は新しいフレームを出したときしか更新されず、
        // 止まっている間の遅れを表せなかった。このクラスはそれを拾うためにある
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(19.2);

        Assert.Null(Measure(monitor, 0, 19.25));
        Assert.Null(Measure(monitor, 100, 19.6));
        string? line = Measure(monitor, 200, 19.9);

        Assert.NotNull(line);
        Assert.Contains("lagMs=-700", line);
    }

    [Fact(DisplayName = "ずれが続く間は間隔をあけて継続を記録する")]
    public void Evaluate_Continuing_ReportsOncePerInterval()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 0, 11.0);

        Assert.Null(Measure(monitor, Persist + Interval - 1, 11.0));
        string? line = Measure(monitor, Persist + Interval, 11.0);

        Assert.NotNull(line);
        Assert.Contains($"継続 {Persist + Interval}ms", line);
        Assert.Null(Measure(monitor, Persist + Interval + 100, 11.0));
    }

    [Fact(DisplayName = "ずれが閾値以内へ戻ったら解消と継続時間を 1 度だけ記録する")]
    public void Evaluate_Recovered_ReportsOnceWithDuration()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 1000, 11.0);

        monitor.NotePresented(12.0);
        string? line = Measure(monitor, 3500, 12.0);

        Assert.NotNull(line);
        Assert.Contains("解消 継続時間=2500ms", line);
        Assert.Null(Measure(monitor, 3600, 12.0));
    }

    [Fact(DisplayName = "遅れている途中で次のフレームの時刻待ちになったら、時刻待ちによる解消として記録する")]
    public void Evaluate_BehindThenNextFrameNotYetDue_ReportsExemptedRecovery()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 0, 10.5);

        string? line = NotYetDue(monitor, 300, 10.6);

        Assert.NotNull(line);
        Assert.Contains("解消（次のフレームの時刻待ち） 継続時間=300ms", line);
    }

    [Fact(DisplayName = "ずれている途中で測れなくなったら打ち切りを理由付きで 1 度だけ記録する")]
    public void Evaluate_LaggingThenUnmeasurable_ReportsAbandonOnceWithReason()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 0, 11.0);

        string? line = Unmeasurable(monitor, 300, 11.0, "シーク後の待ち");

        Assert.NotNull(line);
        Assert.Contains("打ち切り（シーク後の待ち） 継続時間=300ms", line);
        Assert.Null(Unmeasurable(monitor, 400, 11.0, "シーク後の待ち"));
    }

    [Fact(DisplayName = "ずれている途中で提示記録がリセットされたら打ち切りとして記録する")]
    public void Evaluate_LaggingThenReset_ReportsAbandon()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 0, 11.0);

        monitor.Reset();
        string? line = Measure(monitor, 200, 11.0);

        Assert.NotNull(line);
        Assert.Contains("打ち切り（提示記録のリセット", line);
    }

    [Fact(DisplayName = "測れない状態を挟んだ後は、持続の数え直しから始める")]
    public void Evaluate_AfterUnmeasurable_RestartsPersistence()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        StartLagging(monitor, 0, 11.0);
        Assert.NotNull(Unmeasurable(monitor, 200, 11.0, "再生中でない"));

        Assert.Null(Measure(monitor, 300, 11.0));
        string? line = Measure(monitor, 300 + Persist, 11.0);

        Assert.NotNull(line);
        Assert.Contains($"開始（最初に超えた評価から{Persist}ms）", line);
    }

    [Fact(DisplayName = "持続の途中で閾値以内へ戻ったら、持続を数え直す")]
    public void Evaluate_ExcursionInterruptedBeforeStart_RestartsPersistence()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);

        Assert.Null(Measure(monitor, 0, 10.5));
        monitor.NotePresented(10.6);
        Assert.Null(Measure(monitor, 100, 10.6));
        Assert.Null(Measure(monitor, 200, 11.2));
        Assert.NotNull(Measure(monitor, 300, 11.2));
    }

    [Fact(DisplayName = "評価の間隔が短く揺れても、2 回続けて超えれば開始を記録する")]
    public void Evaluate_TwoConsecutiveEvaluations_StartRegardlessOfJitter()
    {
        // 時刻差で判定していたときは、94ms 間隔で 2 回並ぶと 3 回目まで待たされた
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);

        Assert.Null(Measure(monitor, 0, 10.5));
        string? line = Measure(monitor, 94, 10.6);

        Assert.NotNull(line);
        Assert.Contains("開始（最初に超えた評価から94ms）", line);
    }

    [Fact(DisplayName = "Reset 後は前に提示した表示時刻を使わない")]
    public void Reset_ForgetsPresentedPts()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(300.0);

        monitor.Reset();

        Assert.Null(Measure(monitor, 0, 0.0));
        Assert.Null(Measure(monitor, Persist, 0.0));
    }

    [Fact(DisplayName = "直近の区間で提示した枚数を、数えた区間の長さと一緒に載せる")]
    public void Evaluate_ReportsPresentationCountWithSpan()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        for (long t = 0; t < 900; t += 100) Assert.Null(Measure(monitor, t, 10.0));

        // 1 秒の間に 1 枚しか出ない（提示側が詰まっている）
        monitor.NotePresented(10.0);
        Assert.Null(Measure(monitor, 900, 11.0));
        string? line = Measure(monitor, 1000, 11.0);

        Assert.NotNull(line);
        Assert.Contains("直近1000msの提示=1枚", line);
    }

    [Fact(DisplayName = "標本が環状バッファを一巡した後も、直近約 1 秒の枚数を数える")]
    public void Evaluate_AfterSampleBufferWraps_CountsRecentWindow()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        for (long t = 0; t < 2000; t += 100)
        {
            monitor.NotePresented(10.0);
            Assert.Null(Measure(monitor, t, 10.0));
        }

        Assert.Null(Measure(monitor, 2000, 12.0));
        string? line = Measure(monitor, 2100, 12.0);

        Assert.NotNull(line);
        Assert.Contains("直近1000msの提示=", line);
    }

    [Fact(DisplayName = "測れない区間をまたいだ提示枚数は数えない")]
    public void Evaluate_UnmeasurableGap_RestartsPresentationCount()
    {
        var monitor = new VideoLagMonitor();
        monitor.NotePresented(10.0);
        Assert.Null(Measure(monitor, 0, 10.0));
        for (int i = 0; i < 30; i++) monitor.NotePresented(10.0);
        Assert.Null(Unmeasurable(monitor, 500, 10.0, "再生中でない"));

        Assert.Null(Measure(monitor, 600, 11.0));
        string? line = Measure(monitor, 700, 11.0);

        Assert.NotNull(line);
        Assert.Contains("直近100msの提示=0枚", line);
    }
}
