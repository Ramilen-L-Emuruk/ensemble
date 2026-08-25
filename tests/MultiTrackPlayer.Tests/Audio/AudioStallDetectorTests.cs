using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

/// <summary>
/// 音声出力の滞留検出。時刻は呼び出し側が渡す設計なので、実時間を待たずに検証できる。
/// </summary>
public class AudioStallDetectorTests
{
    private const int ThresholdMs = 3000;

    private static AudioStallDetector CreatePrimed(long primeAtTicks = 1000)
    {
        var detector = new AudioStallDetector(ThresholdMs);
        detector.Prime(primeAtTicks);
        return detector;
    }

    [Theory(DisplayName = "閾値未満の経過では報告しない")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ThresholdMs - 1)]
    public void ShouldReport_BelowThreshold_IsFalse(int elapsedMs)
    {
        var detector = CreatePrimed(1000);

        Assert.False(detector.ShouldReport(1000 + elapsedMs));
    }

    [Fact(DisplayName = "閾値に達した時点で報告する（境界値は報告する側）")]
    public void ShouldReport_AtThreshold_IsTrue()
    {
        var detector = CreatePrimed(1000);

        Assert.True(detector.ShouldReport(1000 + ThresholdMs));
    }

    [Fact(DisplayName = "滞留が続いても報告は 1 度だけ")]
    public void ShouldReport_WhileStillStalled_ReportsOnce()
    {
        var detector = CreatePrimed(1000);

        Assert.True(detector.ShouldReport(1000 + ThresholdMs));
        Assert.False(detector.ShouldReport(1000 + ThresholdMs + 100));
        Assert.False(detector.ShouldReport(1000 + ThresholdMs * 10));
    }

    /// <summary>
    /// 「一度きり」が「アプリを再起動するまで二度と」に化けないことの回帰テスト。
    /// 検出器はアプリ寿命の <c>MediaEngine</c> が持つため、抑制が解けないと 2 度目の障害を見逃す
    /// （ensemble-review.md §7 の寿命の食い違い）。
    /// </summary>
    [Fact(DisplayName = "Read が戻れば抑制が解け、次の滞留で改めて報告する")]
    public void ShouldReport_AfterRecovery_ReportsAgain()
    {
        var detector = CreatePrimed(1000);
        Assert.True(detector.ShouldReport(1000 + ThresholdMs));

        detector.NoteRead(10000); // 復帰
        Assert.False(detector.ShouldReport(10000));

        Assert.True(detector.ShouldReport(10000 + ThresholdMs));
    }

    [Fact(DisplayName = "報告後に閾値内へ戻るだけでも抑制は解ける")]
    public void ShouldReport_ObservedWithinThreshold_ClearsSuppression()
    {
        var detector = CreatePrimed(1000);
        Assert.True(detector.ShouldReport(1000 + ThresholdMs));

        detector.NoteRead(10000);
        // ShouldReport を挟まずに次の滞留へ入っても、閾値超過は 1 度報告される
        Assert.True(detector.ShouldReport(10000 + ThresholdMs));
    }

    [Fact(DisplayName = "NoteRead が基準時刻を進める")]
    public void NoteRead_MovesBaseline()
    {
        var detector = CreatePrimed(1000);

        detector.NoteRead(1000 + ThresholdMs - 1);

        // 直前の Read から測り直すため、Prime 時刻からは閾値を超えていても報告しない
        Assert.False(detector.ShouldReport(1000 + ThresholdMs));
        Assert.True(detector.ShouldReport(1000 + ThresholdMs * 2));
    }

    [Fact(DisplayName = "Prime は基準時刻を置き直す")]
    public void Prime_MovesBaseline()
    {
        var detector = CreatePrimed(1000);

        detector.Prime(50000);

        Assert.False(detector.ShouldReport(50000 + ThresholdMs - 1));
        Assert.True(detector.ShouldReport(50000 + ThresholdMs));
    }

    /// <summary>
    /// 一時停止から再生へ戻すと、Read が止まっていた時間がそのまま経過時間になる。
    /// <c>Prime</c> がこれを吸収しないと、復帰直後に必ず誤検出する。
    /// </summary>
    [Fact(DisplayName = "長く滞留した後の Prime は報告済みの抑制も解除する")]
    public void Prime_AfterReport_ClearsSuppression()
    {
        var detector = CreatePrimed(1000);
        Assert.True(detector.ShouldReport(1000 + ThresholdMs));

        detector.Prime(600000); // 10 分後に再生を再開した

        Assert.False(detector.ShouldReport(600000));
        Assert.True(detector.ShouldReport(600000 + ThresholdMs));
    }

    [Fact(DisplayName = "ElapsedSinceLastRead は直近の Read からの経過を返す")]
    public void ElapsedSinceLastRead_MeasuresFromLastRead()
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(500, detector.ElapsedSinceLastRead(1500));

        detector.NoteRead(2000);
        Assert.Equal(1000, detector.ElapsedSinceLastRead(3000));
    }

    [Theory(DisplayName = "IsStalled は ShouldReport と同じ閾値で切り替わる")]
    [InlineData(ThresholdMs - 1, false)]
    [InlineData(ThresholdMs, true)]
    [InlineData(ThresholdMs * 10, true)]
    public void IsStalled_UsesSameThresholdAsShouldReport(int elapsedMs, bool expected)
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(expected, detector.IsStalled(1000 + elapsedMs));
    }

    /// <summary>
    /// 表示側は操作のたびに問い合わせる。その問い合わせが「報告済み」を消費してしまうと、
    /// 記録が 1 行も残らないまま案内だけが出る状態になりうる。
    /// </summary>
    [Fact(DisplayName = "IsStalled を何度呼んでも報告の抑制状態を変えない")]
    public void IsStalled_DoesNotAffectReporting()
    {
        var detector = CreatePrimed(1000);

        for (int i = 0; i < 5; i++)
            Assert.True(detector.IsStalled(1000 + ThresholdMs));

        Assert.True(detector.ShouldReport(1000 + ThresholdMs));
    }

    [Fact(DisplayName = "Read が戻れば IsStalled は自動で false へ戻る")]
    public void IsStalled_AfterRead_IsFalse()
    {
        var detector = CreatePrimed(1000);
        Assert.True(detector.IsStalled(1000 + ThresholdMs));

        detector.NoteRead(1000 + ThresholdMs);

        Assert.False(detector.IsStalled(1000 + ThresholdMs));
    }

    [Theory(DisplayName = "閾値が正でなければ生成を拒否する")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveThreshold(int thresholdMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioStallDetector(thresholdMs));
    }
}
