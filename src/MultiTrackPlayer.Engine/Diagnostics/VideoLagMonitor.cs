namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>ずれを測れる状態か。<see cref="VideoLagMonitor.Evaluate"/> へ渡す。</summary>
internal enum LagObservation
{
    /// <summary>測ってよい。</summary>
    Measurable,

    /// <summary>
    /// 次のフレームはあるが、まだその表示時刻になっていない。最後のフレームが古く見えても、
    /// 低フレームレート・VFR でフレーム間隔が長いだけなので、<b>映像が遅れている側のずれだけ</b>免除する。
    /// 映像が先行している側は免除しない（先行していれば次のフレームは必ず時刻待ちになるため、免除すると見えなくなる）。
    /// </summary>
    NextFrameNotYetDue,

    /// <summary>測れない（一時停止・シーク後の待ち・映像の終端など）。理由を一緒に渡す。</summary>
    NotMeasurable
}

/// <summary>
/// 最後に提示対象にしたフレームが再生クロックからどれだけずれているかを見て、ずれている間だけ診断ログ用の行を返す。
/// </summary>
/// <remarks>
/// <para>
/// <b>ずれは「最後にリースしたフレームの表示時刻 − いまのクロック」で測る。</b>
/// フレームを提示した瞬間の差（<c>PlaybackStatistics.VideoLagSec</c>）ではない。あちらは新しいフレームが
/// 出たときにしか更新されないので、映像が止まっている間は止まる直前の小さな値のまま残る。
/// こちらは映像が止まっていればクロックの前進ぶんだけ差が広がり、止まっていることがそのまま値に出る。
/// リースから実際に画面へ出るまでは vout 1 周ぶん（通常 17〜50ms）遅れるが、しきい値に収まる。
/// </para>
/// <para>
/// <b>提示枚数を一緒に記録するのは、遅れの原因を区別するため。</b>
/// 「1 秒に 1 枚しか出ていない」（提示側が詰まっている）と「60 枚出ているのに遅れたまま」
/// （デコードが実時間ぎりぎりで追いつけない）は、遅れの値だけでは見分けられない。
/// 数える区間は、測れる状態が続いている直近の約 1 秒で、実際の長さを行に載せる。
/// </para>
/// <para>
/// <b>許容は向きで違う。</b> 映像が遅れている側は <see cref="ThresholdSeconds"/>（次のフレームの時刻待ちなら免除）、
/// 先行している側はフレーム間隔の半分＋余裕（<see cref="AheadToleranceSeconds"/>。リースが半フレーム先まで選ぶため）。
/// 遅れ側の免除は滞留検出（<c>IsWaitingForFrameTime</c>）と同じ述語で、クロックの停止や表示時刻の飛びとは
/// 区別できない（同じ死角をあえて揃えている。<c>SlotSequencer.IsWaitingForFrameTime</c> の既知の限界）。
/// </para>
/// <para>
/// <b>拾えるのは続いているずれだけ。</b> 「開始」は評価 2 回続けて閾値を超えたときに記録する。映像が止まると、
/// 遅れが閾値（0.1 秒）を超えるのは止まり始めから約 100ms 後。超えた後の最初の評価は止まり始めから 100〜200ms、
/// 次の評価は 200〜300ms（状態通知は約 100ms 周期で、どこに当たるかは位相による）。<b>確実に拾えるのは約 300ms 以上
/// 続いたもので、200〜300ms は拾えたり拾えなかったりする。</b> 1 周だけ現れる偽のずれ（シーク直後に古いフレームが
/// 残っている瞬間・リースとリセットの入れ違い・低フレームレートのリース直前）を落とすための線引きで、
/// それより短い本物の引っかかりもここには出ない。
/// </para>
/// <para>
/// 短い引っかかりの一部は他の記録に出る。vout の 1 周が 100ms を超えたもの（GPU 経路のみ）は voutSlow、
/// フレームを捨てたものは videoDrop。<b>デコード側で遅れて、フレームを捨てずに追いつく短い遅れ（CPU 経路を含む）は、
/// どの記録にも残らない。</b> 記録が無いことを「途切れが無かった」とは読まないこと。
/// </para>
/// <para>
/// <b>記録は開始・継続・解消・打ち切りの 4 種。</b> 打ち切りは、ずれている途中で測れなくなった
/// （一時停止・シーク・提示記録のリセットなど）ことを表す。これが無いと「開始」だけが残り、
/// ずれたままなのか測るのをやめたのかが読めない。
/// </para>
/// <para>
/// <b>測らない区間がある。</b> <see cref="Reset"/> の後、次のフレームが提示されるまで（シーク後の最初の
/// フレームが遅れる場合を含む）と、シーク後の待ちの間は測らない。そこで映像が出てこない障害は
/// 滞留検出（<c>StallDetector</c>）が経過時間で拾う。
/// </para>
/// <para>
/// スレッドの分担: <see cref="NotePresented"/> は提示側（vout スレッド・UI スレッド）から、<see cref="Reset"/> は
/// パイプライン構築側とシーク処理（demux スレッド）から、<see cref="Evaluate"/> は状態通知のタイマー（重複起動しない）から呼ぶ。
/// <see cref="Evaluate"/> だけが触るフィールドは単一スレッドからしか読み書きされない。
/// </para>
/// </remarks>
internal sealed class VideoLagMonitor
{
    /// <summary>これを超えてずれている間を「ずれている」とみなす。1 フレーム（60fps で約 17ms）と状態通知の周期（100ms）の揺れを吸収する値。</summary>
    public const double ThresholdSeconds = 0.1;

    /// <summary>
    /// 映像が先行する側の許容に、フレーム間隔の半分へ足す余裕。リースは表示時刻がクロック＋フレーム間隔の半分
    /// 以内のフレームを選ぶので、健全でもその分は先行する。低フレームレートで <see cref="ThresholdSeconds"/> を超える。
    /// </summary>
    public const double AheadSlackSeconds = 0.05;

    /// <summary>ずれが続いている間、記録を繰り返す間隔。提示枚数もおよそこの長さの区間で数える。</summary>
    public const long ReportIntervalMs = 1000;

    // 提示枚数を数えるための標本の数。状態通知は 100ms 周期なので、これで約 1 秒ぶんを持てる
    private const int RateSampleCount = 11;

    // 提示側スレッドが書き、Evaluate が読む
    private double _presentedPtsSec = double.NaN;
    private long _presentedCount;

    // ここから下は Evaluate だけが触る
    private bool _lagging;
    private long _laggingSinceTicks;
    // まだ「開始」していないとき、前回の評価で閾値を超えていればその評価の時刻（超えていなければ -1）。
    // 次の評価でも超えていれば開始とみなし、継続時間の起点にする（実際の止まり始めはこれより前）
    private long _exceededSinceTicks = -1;
    private long _lastReportTicks;
    private readonly long[] _sampleTicks = new long[RateSampleCount];
    private readonly long[] _sampleCounts = new long[RateSampleCount];
    private int _sampleNext;
    private int _sampleFilled;

    /// <summary>新しいフレームを提示対象にしたときに呼ぶ（同じフレームの再提示では呼ばない）。</summary>
    public void NotePresented(double ptsSeconds)
    {
        Volatile.Write(ref _presentedPtsSec, ptsSeconds);
        Interlocked.Increment(ref _presentedCount);
    }

    /// <summary>
    /// 提示済みフレームの記憶を捨てる。パイプラインを作り直すときと、シークで映像リングを掃除するときに呼ぶ
    /// （前のファイル・シーク前の表示時刻と新しいクロックを比べて偽のずれを出さないため）。
    /// 次に <see cref="NotePresented"/> が呼ばれるまでは測らない。
    /// </summary>
    public void Reset() => Volatile.Write(ref _presentedPtsSec, double.NaN);

    /// <summary>
    /// いまのずれを評価し、記録すべき行があれば返す。診断ログが無効の間は、<see cref="LagObservation.NotMeasurable"/>
    /// で呼んで状態を捨てさせること（呼ばない間に状態が古くなり、有効にした直後の行が無効だった期間を含んでしまう）。
    /// </summary>
    /// <param name="nowTicks">ミリ秒単位の単調な時刻。</param>
    /// <param name="clockSeconds">いまの再生クロック（秒）。</param>
    /// <param name="frameDurationSeconds">フレーム間隔（秒）。映像が先行する側の許容に使う。</param>
    /// <param name="observation">ずれを測れる状態か。</param>
    /// <param name="notMeasurableReason">
    /// <paramref name="observation"/> が <see cref="LagObservation.NotMeasurable"/> のときの理由（打ち切りの行に載せる）。
    /// それ以外では使わない。
    /// </param>
    /// <returns>記録する行。無ければ null。</returns>
    public string? Evaluate(
        long nowTicks, double clockSeconds, double frameDurationSeconds,
        LagObservation observation, string? notMeasurableReason)
    {
        double presented = Volatile.Read(ref _presentedPtsSec);
        if (observation == LagObservation.NotMeasurable || double.IsNaN(presented))
        {
            // 測れない区間をまたいだ提示枚数と持続は意味を持たないので、数え直す
            _sampleFilled = 0;
            _exceededSinceTicks = -1;
            string reason = observation == LagObservation.NotMeasurable
                ? notMeasurableReason ?? "理由不明"
                : "提示記録のリセット（シーク・開き直し）";
            return EndLagging($"打ち切り（{reason}）", nowTicks, clockSeconds, presented);
        }

        AddRateSample(nowTicks);

        double lagSec = presented - clockSeconds;
        bool behind = lagSec < -ThresholdSeconds;
        bool ahead = lagSec > AheadToleranceSeconds(frameDurationSeconds);
        // 時刻待ちで説明できるのは「最後のフレームが古い」側だけ。映像が先行しているのは、時刻待ちと同時に
        // 起きていても（先行していれば次のフレームは必ずまだ時刻になっていない）ずれとして扱う
        bool exempted = behind && observation == LagObservation.NextFrameNotYetDue;
        if ((!behind && !ahead) || exempted)
        {
            _exceededSinceTicks = -1;
            return EndLagging(exempted ? "解消（次のフレームの時刻待ち）" : "解消", nowTicks, clockSeconds, presented);
        }

        if (!_lagging)
        {
            // 評価 2 回続けて超えたら開始する（クラスの remarks「拾える下限」）
            if (_exceededSinceTicks < 0)
            {
                _exceededSinceTicks = nowTicks;
                return null;
            }
            _lagging = true;
            _laggingSinceTicks = _exceededSinceTicks;
            _lastReportTicks = nowTicks;
            return Describe($"開始（最初に超えた評価から{nowTicks - _laggingSinceTicks}ms）", clockSeconds, presented);
        }
        if (nowTicks - _lastReportTicks < ReportIntervalMs) return null;
        _lastReportTicks = nowTicks;
        return Describe($"継続 {nowTicks - _laggingSinceTicks}ms", clockSeconds, presented);
    }

    /// <summary>映像が先行する側の許容。フレーム間隔の半分＋余裕と、通常の閾値の大きいほう。</summary>
    public static double AheadToleranceSeconds(double frameDurationSeconds) =>
        Math.Max(ThresholdSeconds, frameDurationSeconds / 2.0 + AheadSlackSeconds);

    // ずれている途中なら終わりを 1 行だけ記録する。ずれていなければ何もしない
    private string? EndLagging(string phase, long nowTicks, double clockSeconds, double presented)
    {
        if (!_lagging) return null;
        _lagging = false;
        return Describe($"{phase} 継続時間={nowTicks - _laggingSinceTicks}ms", clockSeconds, presented);
    }

    private void AddRateSample(long nowTicks)
    {
        _sampleTicks[_sampleNext] = nowTicks;
        _sampleCounts[_sampleNext] = Interlocked.Read(ref _presentedCount);
        _sampleNext = (_sampleNext + 1) % RateSampleCount;
        if (_sampleFilled < RateSampleCount) _sampleFilled++;
    }

    // 直近の標本と、残っている中で最も古い標本の差から、提示枚数とその区間の長さを書く
    private string DescribeRate()
    {
        if (_sampleFilled < 2) return "提示枚数=未計測";
        int newest = (_sampleNext - 1 + RateSampleCount) % RateSampleCount;
        int oldest = (_sampleNext - _sampleFilled + RateSampleCount) % RateSampleCount;
        long spanMs = _sampleTicks[newest] - _sampleTicks[oldest];
        long frames = _sampleCounts[newest] - _sampleCounts[oldest];
        return $"直近{spanMs}msの提示={frames}枚";
    }

    private string Describe(string phase, double clockSeconds, double presented)
    {
        string lag = double.IsNaN(presented) ? "lagMs=不明" : $"lagMs={(presented - clockSeconds) * 1000:F0}（負=映像が遅れている）";
        string pts = double.IsNaN(presented) ? "なし" : presented.ToString("F3");
        return $"映像のずれ {phase} {lag} 最後にリースしたpts={pts} clock={clockSeconds:F3} {DescribeRate()}";
    }
}
