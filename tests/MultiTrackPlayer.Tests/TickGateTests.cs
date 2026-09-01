using MultiTrackPlayer.Engine;

namespace MultiTrackPlayer.Tests;

/// <summary>
/// 周期タイマーの重複起動を弾く門。時刻に依存しないので実時間を待たずに検証できる。
/// </summary>
public class TickGateTests
{
    private const int SustainedThreshold = 10;
    private const int IntermittentThreshold = 50;

    private static TickGate Create() => new(SustainedThreshold, IntermittentThreshold);

    /// <summary>局面だけを見たいテスト用（回数は個別のテストで確かめる）。</summary>
    private static TickSkipReport Skip(TickGate gate) => gate.NoteSkip(out _, out _);

    /// <summary>入っている周を挟まずに「連続 n 回弾かれた」状態を作る。</summary>
    private static TickSkipReport SkipTimes(TickGate gate, int times)
    {
        var last = TickSkipReport.None;
        for (int i = 0; i < times; i++) last = Skip(gate);
        return last;
    }

    [Fact(DisplayName = "誰も走っていなければ入れる")]
    public void TryEnter_WhenIdle_Succeeds()
    {
        Assert.True(Create().TryEnter());
    }

    [Fact(DisplayName = "走行中は弾く")]
    public void TryEnter_WhileRunning_Fails()
    {
        var gate = Create();
        Assert.True(gate.TryEnter());

        Assert.False(gate.TryEnter());
    }

    [Fact(DisplayName = "出れば再び入れる")]
    public void TryEnter_AfterExit_Succeeds()
    {
        var gate = Create();
        gate.TryEnter();

        gate.Exit();

        Assert.True(gate.TryEnter());
    }

    // ── 連続の詰まり（Sustained）──

    [Fact(DisplayName = "連続が閾値未満なら記録を促さない")]
    public void NoteSkip_BelowSustainedThreshold_ReportsNone()
    {
        var gate = Create();
        gate.TryEnter();

        for (int i = 1; i < SustainedThreshold; i++)
        {
            Assert.Equal(TickSkipReport.None, gate.NoteSkip(out int consecutive, out int total));
            Assert.Equal(i, consecutive);
            Assert.Equal(i, total);
        }
    }

    [Fact(DisplayName = "連続が閾値に達したとき 1 度だけ記録を促す")]
    public void NoteSkip_AtSustainedThreshold_ReportsSustainedOnce()
    {
        var gate = Create();
        gate.TryEnter();
        SkipTimes(gate, SustainedThreshold - 1);

        Assert.Equal(TickSkipReport.Sustained, gate.NoteSkip(out int consecutive, out _));
        Assert.Equal(SustainedThreshold, consecutive);

        // 詰まりが続く間は 2 度目を促さない（記録が溢れる）
        Assert.Equal(TickSkipReport.None, Skip(gate));
        Assert.Equal(TickSkipReport.None, Skip(gate));
    }

    [Fact(DisplayName = "出れば連続の数えは振り出しに戻る")]
    public void Exit_ResetsConsecutiveSkips()
    {
        var gate = Create();
        gate.TryEnter();
        SkipTimes(gate, SustainedThreshold - 1);

        gate.Exit();
        gate.TryEnter();

        Assert.Equal(TickSkipReport.None, gate.NoteSkip(out int consecutive, out _));
        Assert.Equal(1, consecutive);
    }

    /// <summary>
    /// 「一度きり」が「この門が生きている間は二度と」に化けないことの回帰テスト。
    /// 抑制を門の寿命に紐づけると、一度詰まって回復した後の 2 度目の詰まりが報告されない
    /// （<c>ensemble-review.md</c> §7 の寿命の食い違い）。
    /// </summary>
    [Fact(DisplayName = "回復した後に再び詰まれば、改めて Sustained を促す")]
    public void NoteSkip_AfterRecovery_ReportsSustainedAgain()
    {
        var gate = Create();

        // 累計の閾値（50）に届かない範囲で 3 回の詰まりを起こす（10 × 3 = 30）
        for (int episode = 0; episode < 3; episode++)
        {
            gate.TryEnter();
            Assert.Equal(TickSkipReport.None, SkipTimes(gate, SustainedThreshold - 1));
            Assert.Equal(TickSkipReport.Sustained, Skip(gate));

            gate.Exit(); // 詰まりが解消した
        }
    }

    // ── 断続的な詰まり（Intermittent）──
    //
    // 連続の閾値だけだと、その手前で回復し続けるパターンが記録に一切現れない。
    // 「連続 9 回 → 1 回成功 → 連続 9 回 → …」は永遠に 10 へ届かないのに、
    // その間ずっと本体（位置通知・3 つの滞留検出・再生終了の判定）は間引かれている。

    [Fact(DisplayName = "連続では届かなくても、累計が閾値に達すれば記録を促す")]
    public void NoteSkip_WhenFlapping_ReportsIntermittent()
    {
        var gate = Create();
        var reports = new List<TickSkipReport>();

        // 1 周ごとに「連続 9 回スキップ → 1 回成功」を繰り返す。連続は 10 に届かない
        for (int round = 0; round < 10; round++)
        {
            gate.TryEnter();
            for (int i = 0; i < SustainedThreshold - 1; i++) reports.Add(Skip(gate));
            gate.Exit();
        }

        Assert.DoesNotContain(TickSkipReport.Sustained, reports);
        Assert.Single(reports, r => r == TickSkipReport.Intermittent);
    }

    [Fact(DisplayName = "累計は出ても戻らない")]
    public void Exit_DoesNotResetTotalSkips()
    {
        var gate = Create();

        gate.TryEnter();
        SkipTimes(gate, 3);
        gate.Exit();

        gate.TryEnter();
        gate.NoteSkip(out int consecutive, out int total);

        Assert.Equal(1, consecutive);
        Assert.Equal(4, total);
    }

    [Fact(DisplayName = "断続の記録は門 1 つにつき 1 度だけ")]
    public void NoteSkip_Intermittent_ReportsOncePerGate()
    {
        var gate = Create();
        var reports = new List<TickSkipReport>();

        // 連続は 9 回で止め続けて Sustained を出さないまま、累計の閾値を大きく超えさせる
        for (int round = 0; round < 30; round++)
        {
            gate.TryEnter();
            for (int i = 0; i < SustainedThreshold - 1; i++) reports.Add(Skip(gate));
            gate.Exit();
        }

        Assert.DoesNotContain(TickSkipReport.Sustained, reports);
        Assert.Single(reports, r => r == TickSkipReport.Intermittent);
    }

    /// <summary>
    /// <b>単発の長い詰まりを「断続的」と呼ばせないための回帰テスト。</b>
    /// 2 つのカウンタは独立なので、<c>Exit</c> を挟まない 1 つの詰まりでは連続が累計の閾値も
    /// 超えて両方が発火しうる。そのとき「連続では閾値に届いていない」という断続側の文面は
    /// 事実と正反対になり、直前に出した Sustained の行と矛盾する。
    /// </summary>
    [Fact(DisplayName = "連続の詰まりを報告した後は、累計が積み上がっても断続を報告しない")]
    public void NoteSkip_AfterSustained_NeverReportsIntermittent()
    {
        var gate = Create();
        var reports = new List<TickSkipReport>();

        gate.TryEnter();
        // 1 つの詰まりのまま累計の閾値を大きく超えさせる
        for (int i = 0; i < IntermittentThreshold * 3; i++) reports.Add(Skip(gate));

        Assert.Single(reports, r => r == TickSkipReport.Sustained);
        Assert.DoesNotContain(TickSkipReport.Intermittent, reports);
    }

    /// <summary>
    /// <b>抑制が「二度と」に化けないことの回帰テスト。</b> 連続の詰まりを 1 度報告したことで
    /// 断続の検出を永久に止めると、<b>一度長く詰まっただけで以後そのタイマーの残り時間ずっと
    /// 断続が記録されなくなる</b>——しかも止まったことの痕跡も出ない。
    /// 抑制は「いま続いている詰まり」に紐づけること。
    /// </summary>
    [Fact(DisplayName = "連続の詰まりから回復した後は、断続を報告できる")]
    public void NoteSkip_AfterSustainedThenRecovery_CanStillReportIntermittent()
    {
        var gate = Create();
        var reports = new List<TickSkipReport>();

        // まず 1 度、連続の詰まりを起こして回復する
        gate.TryEnter();
        for (int i = 0; i < SustainedThreshold; i++) reports.Add(Skip(gate));
        gate.Exit();
        Assert.Single(reports, r => r == TickSkipReport.Sustained);

        int reportsAfterRecovery = reports.Count;

        // 以降は連続の閾値に届かない振動を繰り返し、累計を大きく超えさせる
        for (int round = 0; round < 30; round++)
        {
            gate.TryEnter();
            for (int i = 0; i < SustainedThreshold - 1; i++) reports.Add(Skip(gate));
            gate.Exit();
        }

        Assert.Single(reports.Skip(reportsAfterRecovery), r => r == TickSkipReport.Intermittent);
    }

    /// <summary>
    /// <b>同じ詰まりで 2 行出る経路を固定する。</b> 抑制は <c>Sustained</c> → <c>Intermittent</c> の
    /// 一方向だけで、逆向きは見ていない。2 つの数えが独立で累計が <c>Exit</c> で戻らないため、
    /// 「累計が閾値の手前まで積み上がった状態で新しい詰まりが始まる」と
    /// <c>Intermittent</c> → <c>Sustained</c> の順で両方が発火する。
    /// </summary>
    /// <remarks>
    /// <b>競合ではなく決定的に起きる</b>ので、このテストは単一スレッドで再現できる。
    /// 仕様として受け入れている挙動（実装側の remarks に理由がある）なので、
    /// <b>ここで固定するのは「2 行出ること」そのもの</b>——将来これを競合と誤解して
    /// 逆向きの抑制を足すと、より重い <c>Sustained</c> を落とすことになる。
    /// </remarks>
    [Fact(DisplayName = "累計が先に閾値へ達すると、同じ詰まりで Intermittent → Sustained の順に返る")]
    public void NoteSkip_WhenTotalReachesThresholdFirst_ReportsIntermittentThenSustained()
    {
        var gate = Create();

        // 連続 9 回で回復する詰まりを 5 周。累計 45 まで積み、連続は一度も 10 に届かせない
        for (int round = 0; round < 5; round++)
        {
            gate.TryEnter();
            Assert.Equal(TickSkipReport.None, SkipTimes(gate, SustainedThreshold - 1));
            gate.Exit();
        }

        // 新しい詰まり。Exit を挟まないので連続が伸び続ける
        gate.TryEnter();

        // 連続 5 回目で累計が 50 に達する
        for (int i = 0; i < 4; i++) Assert.Equal(TickSkipReport.None, Skip(gate));
        Assert.Equal(TickSkipReport.Intermittent, gate.NoteSkip(out int atIntermittent, out int total));
        Assert.Equal(5, atIntermittent);
        Assert.Equal(IntermittentThreshold, total);

        // 同じ詰まりのまま連続 10 回目で Sustained
        for (int i = 0; i < 4; i++) Assert.Equal(TickSkipReport.None, Skip(gate));
        Assert.Equal(TickSkipReport.Sustained, gate.NoteSkip(out int atSustained, out _));
        Assert.Equal(SustainedThreshold, atSustained);
    }

    // ── 生成時の検証 ──

    [Theory(DisplayName = "閾値が正でなければ生成を拒否する")]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void Constructor_RejectsNonPositiveThresholds(int sustained, int intermittent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickGate(sustained, intermittent));
    }

    /// <summary>
    /// 累計の閾値が連続の閾値より小さいと、累計側が必ず先に発火して連続の判定が死ぬ。
    /// 設定として意味を持たないので生成時に弾く。
    /// </summary>
    [Fact(DisplayName = "累計の閾値が連続の閾値より小さければ生成を拒否する")]
    public void Constructor_RejectsIntermittentThresholdBelowSustained()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickGate(10, 9));
    }

    [Fact(DisplayName = "累計と連続の閾値が同じ値なら受け入れる（境界）")]
    public void Constructor_AcceptsEqualThresholds()
    {
        Assert.NotNull(new TickGate(10, 10));
    }

    // ── 排他そのもの ──

    /// <summary>
    /// 門の存在意義そのもの。同時に殺到しても本体を走らせるのは 1 つだけ。
    /// </summary>
    /// <remarks>
    /// <b>偽陽性は出ない。</b> <c>Interlocked.CompareExchange</c> が効いていれば、
    /// 同時に入れるのは原理的に 1 つ。逆に窓を踏み外せば見逃すので、
    /// <b>通ったことは保証ではなく反証の不在</b>。
    /// </remarks>
    [Fact(DisplayName = "同時に殺到しても入れるのは 1 つだけ")]
    public async Task TryEnter_UnderContention_AdmitsExactlyOne()
    {
        const int rounds = 20_000;

        var gate = Create();
        int totalAdmitted = 0;

        void Hammer()
        {
            for (int i = 0; i < rounds; i++)
                if (gate.TryEnter()) Interlocked.Increment(ref totalAdmitted);
        }

        // 一度も Exit しないので、全体を通して入れるのは 1 つだけであるべき
        var other = Task.Run(Hammer);
        Hammer();
        await other;

        Assert.Equal(1, totalAdmitted);
    }
}
