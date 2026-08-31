using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Diagnostics;

/// <summary>
/// 滞留検出（音声出力の <c>Read</c> と映像フレームの提示で共用）。
/// 時刻は呼び出し側が渡す設計なので、実時間を待たずに検証できる。
/// </summary>
public class StallDetectorTests
{
    private const int ThresholdMs = 3000;

    private static StallDetector CreatePrimed(long primeAtTicks = 1000)
    {
        var detector = new StallDetector(ThresholdMs);
        detector.Prime(primeAtTicks);
        return detector;
    }

    /// <summary>局面だけを見たいテスト用（滞留時間は個別のテストで確かめる）。</summary>
    private static StallPhase PhaseAt(StallDetector detector, long nowTicks) => detector.Poll(nowTicks).Phase;

    [Theory(DisplayName = "閾値未満の経過では何も報告しない")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ThresholdMs - 1)]
    public void Poll_BelowThreshold_IsRunning(int elapsedMs)
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(StallPhase.Running, PhaseAt(detector, 1000 + elapsedMs));
    }

    [Fact(DisplayName = "閾値に達した時点で報告する（境界値は報告する側）")]
    public void Poll_AtThreshold_IsTrue()
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));
    }

    [Fact(DisplayName = "滞留が続いても報告は 1 度だけ")]
    public void Poll_WhileStillStalled_ReportsOnce()
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));
        Assert.Equal(StallPhase.Continuing, PhaseAt(detector, 1000 + ThresholdMs + 100));
        Assert.Equal(StallPhase.Continuing, PhaseAt(detector, 1000 + ThresholdMs * 10));
    }

    /// <summary>
    /// 「一度きり」が「アプリを再起動するまで二度と」に化けないことの回帰テスト。
    /// 検出器はアプリ寿命の <c>MediaEngine</c> が持つため、抑制が解けないと 2 度目の障害を見逃す
    /// （ensemble-review.md §7 の寿命の食い違い）。
    /// </summary>
    [Fact(DisplayName = "活動が戻れば抑制が解け、次の滞留で改めて報告する")]
    public void Poll_AfterRecovery_ReportsAgain()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        detector.NoteActivity(10000); // 復帰
        Assert.Equal(StallPhase.Recovered, PhaseAt(detector, 10000));

        Assert.Equal(StallPhase.Started, PhaseAt(detector, 10000 + ThresholdMs));
    }

    [Fact(DisplayName = "報告後に閾値内へ戻るだけでも抑制は解ける")]
    public void Poll_ObservedWithinThreshold_ClearsSuppression()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        detector.NoteActivity(10000);
        // Poll を挟まずに次の滞留へ入っても、閾値超過は 1 度報告される（回復の局面は取りこぼす）
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 10000 + ThresholdMs));
    }

    [Fact(DisplayName = "NoteActivity が基準時刻を進める")]
    public void NoteActivity_MovesBaseline()
    {
        var detector = CreatePrimed(1000);

        detector.NoteActivity(1000 + ThresholdMs - 1);

        // 直前の活動から測り直すため、Prime 時刻からは閾値を超えていても報告しない
        Assert.Equal(StallPhase.Running, PhaseAt(detector, 1000 + ThresholdMs));
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs * 2));
    }

    [Fact(DisplayName = "Prime は基準時刻を置き直す")]
    public void Prime_MovesBaseline()
    {
        var detector = CreatePrimed(1000);

        detector.Prime(50000);

        Assert.Equal(StallPhase.Running, PhaseAt(detector, 50000 + ThresholdMs - 1));
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 50000 + ThresholdMs));
    }

    /// <summary>
    /// 一時停止から再生へ戻すと、活動が止まっていた時間がそのまま経過時間になる。
    /// <c>Prime</c> がこれを吸収しないと、復帰直後に必ず誤検出する。
    /// </summary>
    [Fact(DisplayName = "長く滞留した後の Prime は報告済みの抑制も解除する")]
    public void Prime_AfterReport_ClearsSuppression()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        detector.Prime(600000); // 10 分後に再生を再開した

        // Prime は報告済みの抑制ごと忘れるので、回復ではなく Running から始まる
        Assert.Equal(StallPhase.Running, PhaseAt(detector, 600000));
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 600000 + ThresholdMs));
    }

    [Fact(DisplayName = "ElapsedSinceLastActivity は直近の活動からの経過を返す")]
    public void ElapsedSinceLastActivity_MeasuresFromLastActivity()
    {
        var detector = CreatePrimed(1000);

        Assert.Equal(500, detector.ElapsedSinceLastActivity(1500));

        detector.NoteActivity(2000);
        Assert.Equal(1000, detector.ElapsedSinceLastActivity(3000));
    }

    [Theory(DisplayName = "IsStalled は Poll と同じ閾値で切り替わる")]
    [InlineData(ThresholdMs - 1, false)]
    [InlineData(ThresholdMs, true)]
    [InlineData(ThresholdMs * 10, true)]
    public void IsStalled_UsesSameThresholdAsPoll(int elapsedMs, bool expected)
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

        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));
    }

    [Fact(DisplayName = "活動が戻れば IsStalled は自動で false へ戻る")]
    public void IsStalled_AfterRead_IsFalse()
    {
        var detector = CreatePrimed(1000);
        Assert.True(detector.IsStalled(1000 + ThresholdMs));

        detector.NoteActivity(1000 + ThresholdMs);

        Assert.False(detector.IsStalled(1000 + ThresholdMs));
    }

    [Theory(DisplayName = "閾値が正でなければ生成を拒否する")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveThreshold(int thresholdMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StallDetector(thresholdMs));
    }

    // ── 回復の局面（Recovered）──
    //
    // 滞留の開始しか報告できないと、記録に「Nms 活動が無い」の 1 行だけが残り、
    // 3 秒で戻ったのか永久に止まったのかが事後に分からない。閾値の調整材料にもならない。

    [Fact(DisplayName = "報告済みの滞留から活動が戻ると回復を 1 度返す")]
    public void Poll_AfterActivityResumes_ReportsRecoveredOnce()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        detector.NoteActivity(9000);

        Assert.Equal(StallPhase.Recovered, PhaseAt(detector, 9000));
        // 2 度は返さない
        Assert.Equal(StallPhase.Running, PhaseAt(detector, 9100));
    }

    [Fact(DisplayName = "回復が返す滞留時間は活動が途切れていた実測値")]
    public void Poll_Recovered_ReportsMeasuredGap()
    {
        var detector = CreatePrimed(1000);
        // 最後の活動は 1000。閾値超過を 5000 で報告する
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 5000));

        detector.NoteActivity(9500); // 1000 → 9500 の 8500ms 途切れていた

        var result = detector.Poll(9500);
        Assert.Equal(StallPhase.Recovered, result.Phase);
        Assert.Equal(8500, result.StalledForMs);
    }

    [Fact(DisplayName = "報告していない滞留は回復を返さない")]
    public void Poll_WithoutPriorReport_DoesNotReportRecovered()
    {
        var detector = CreatePrimed(1000);
        // 閾値未満で活動が戻っただけ（滞留として報告していない）
        detector.NoteActivity(1000 + ThresholdMs - 1);

        Assert.Equal(StallPhase.Running, PhaseAt(detector, 1000 + ThresholdMs - 1));
    }

    [Fact(DisplayName = "滞留が続いている間は継続を返す")]
    public void Poll_WhileStillStalled_ReportsContinuing()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        Assert.Equal(StallPhase.Continuing, PhaseAt(detector, 1000 + ThresholdMs + 100));
        Assert.Equal(StallPhase.Continuing, PhaseAt(detector, 1000 + ThresholdMs * 10));
    }

    [Fact(DisplayName = "開始が返す滞留時間は報告時点の経過")]
    public void Poll_Started_ReportsElapsedAtReport()
    {
        var detector = CreatePrimed(1000);

        var result = detector.Poll(1000 + ThresholdMs + 250);

        Assert.Equal(StallPhase.Started, result.Phase);
        Assert.Equal(ThresholdMs + 250, result.StalledForMs);
    }

    [Fact(DisplayName = "Prime は回復の報告予定も忘れる")]
    public void Prime_ForgetsPendingRecovery()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        // 利用者が操作した（再生開始・シーク）ときの置き直し。ここで回復の行は取りこぼす
        detector.Prime(20000);
        detector.NoteActivity(20100);

        Assert.Equal(StallPhase.Running, PhaseAt(detector, 20100));
    }

    // ── 打ち切りの申告（Prime の戻り値）──
    //
    // Prime は報告済みの滞留を捨てる。捨てたことを呼び出し側へ伝えないと、記録に
    // 「滞留の開始だけがある」状態が生まれ、①まだ止まったまま ②利用者操作で追跡を打ち切った、
    // の 2 通りが同じ見た目になる。しかも固まったときに利用者が最も取る操作（一時停止→再生・
    // シークし直す）がそのまま②を踏むため、この機能が一番要る場面で記録が読めなくなる。

    [Fact(DisplayName = "報告済みの滞留を抱えたまま Prime すると打ち切りを申告する")]
    public void Prime_WithPendingReport_ReportsDiscard()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        Assert.NotNull(detector.Prime(20000));
    }

    /// <summary>
    /// 打ち切りは滞留の何時間も後・別のファイルを開いた後に起こりうるため、記録の行は
    /// 「いつの滞留の後始末か」を自力で示す必要がある。返す値がその材料。
    /// </summary>
    [Fact(DisplayName = "打ち切りの申告は滞留が始まってからの経過を返す")]
    public void Prime_WithPendingReport_ReportsElapsedSinceStallBegan()
    {
        var detector = CreatePrimed(1000);
        // 最後の活動は 1000。滞留の開始を 1000 + ThresholdMs で報告する
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        // 1000（滞留の直前の活動）から 20000 までの 19000ms
        Assert.Equal(19000, detector.Prime(20000));
    }

    [Fact(DisplayName = "報告していなければ Prime は打ち切りを申告しない")]
    public void Prime_WithoutPendingReport_DoesNotReportDiscard()
    {
        var detector = new StallDetector(ThresholdMs);

        // 初回（まだ何も報告していない）
        Assert.Null(detector.Prime(1000));
        // 閾値未満で活動が戻っただけ（滞留として報告していない）
        detector.NoteActivity(1000 + ThresholdMs - 1);
        Assert.Null(detector.Prime(20000));
    }

    [Fact(DisplayName = "回復を回収した後の Prime は打ち切りを申告しない")]
    public void Prime_AfterRecoveryCollected_DoesNotReportDiscard()
    {
        var detector = CreatePrimed(1000);
        Assert.Equal(StallPhase.Started, PhaseAt(detector, 1000 + ThresholdMs));

        detector.NoteActivity(9000);
        Assert.Equal(StallPhase.Recovered, PhaseAt(detector, 9000));

        // 回復の行は既に出ているので、打ち切るものは残っていない
        Assert.Null(detector.Prime(20000));
    }

    // ── 対として読み書きすることの回帰テスト ──

    /// <summary>
    /// <c>Prime</c> と <c>Poll</c> が 2 つのフィールドを対として扱わないと、
    /// <b>起きていない回復を「もっともらしい滞留時間つきで」報告する</b>。
    /// <c>Prime</c> が「最後の活動」を新しくした直後（報告済みの基準はまだ古い）に
    /// <c>Poll</c> が割り込むと、閾値内かつ基準が食い違う状態＝回復と判定されてしまう。
    /// </summary>
    /// <remarks>
    /// <b>このテストは偽陽性を出さない。</b> ロックが効いていれば <c>Recovered</c> は
    /// <b>原理的に</b>返らない（<c>Prime</c> が原子的なら、報告済みの基準は必ず未報告と対になる）。
    /// 一方、検出は確率的で<b>窓を踏み外せば見逃す</b>。反復回数はそこから決めてある——
    /// ロックを外して実測したところ 2 万回では 3 回に 1 回ほど見逃し、10 万回で 5 回連続して
    /// 検出できた（所要 120ms 程度）。<b>通ったことは保証ではなく反証の不在</b>なので、
    /// ロック範囲を狭める変更をするなら、このテストが通ったことだけを根拠にしないこと。
    /// </remarks>
    [Fact(DisplayName = "Prime と Poll が競合しても起きていない回復を報告しない")]
    public async Task Poll_RacingWithPrime_NeverFabricatesRecovery()
    {
        // ループ中に「本物の」滞留も回復も起きない大きさ。観測されうる Recovered は偽物だけになる
        const int wideThreshold = 1_000_000;
        const int iterations = 100_000;

        var detector = new StallDetector(wideThreshold);
        using var barrier = new Barrier(2);
        StallPollResult? fabricated = null;

        // **バリアには必ずタイムアウトを置く。** 片側が assert で抜けたとき、もう片側は
        // 永久に待つ。そうなると症状が「assert 失敗」ではなく**テストのハング**として出て、
        // ランナーのタイムアウト頼みになり何が壊れたか読めない。落ちるべきときに落ちない
        // テストは無いより悪い。値は「正常時には絶対に届かない」程度に大きく取る
        const int barrierTimeoutMs = 10_000;

        var poller = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                long baseTicks = (long)i * wideThreshold * 4;
                // false = 主スレッドが抜けた。自分も抜ける（await poller が返れるようにする）
                if (!barrier.SignalAndWait(barrierTimeoutMs)) return;
                var result = detector.Poll(baseTicks + wideThreshold + 1);
                if (result.Phase == StallPhase.Recovered) fabricated ??= result;
                if (!barrier.SignalAndWait(barrierTimeoutMs)) return;
            }
        });

        try
        {
            for (int i = 0; i < iterations; i++)
            {
                long baseTicks = (long)i * wideThreshold * 4;
                // 各周回で「報告済みの滞留を抱えた状態」を作り直す（これが競合の前提）
                detector.Prime(baseTicks);
                Assert.Equal(StallPhase.Started, PhaseAt(detector, baseTicks + wideThreshold));

                Assert.True(barrier.SignalAndWait(barrierTimeoutMs), "バリアの同期がタイムアウトした");
                detector.Prime(baseTicks + wideThreshold + 1);
                Assert.True(barrier.SignalAndWait(barrierTimeoutMs), "バリアの同期がタイムアウトした");
            }
        }
        finally
        {
            // 主スレッドが assert で抜けた場合もここを通す。待ち側はタイムアウトで抜けるので
            // 高々 barrierTimeoutMs で戻り、assert の失敗がそのまま報告される
            await poller;
        }

        Assert.Null(fabricated);
    }
}
