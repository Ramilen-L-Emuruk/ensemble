namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>滞留の局面。<see cref="StallDetector.Poll"/> が返す。</summary>
public enum StallPhase
{
    /// <summary>活動が来ている（報告するものは無い）。</summary>
    Running,

    /// <summary>閾値を超えた。<b>この局面を返すのは 1 つの滞留につき 1 度だけ。</b></summary>
    Started,

    /// <summary>滞留が続いている（既に報告済み）。</summary>
    Continuing,

    /// <summary>
    /// 報告済みの滞留から活動が戻った。<b>この局面を返すのも 1 度だけ。</b>
    /// </summary>
    Recovered
}

/// <param name="Phase">局面。</param>
/// <param name="StalledForMs">
/// <see cref="StallPhase.Started"/> なら「活動が来ていない時間」、
/// <see cref="StallPhase.Recovered"/> なら「活動が途切れていた実測時間」。他の局面では 0。
/// </param>
public readonly record struct StallPollResult(StallPhase Phase, long StalledForMs);

/// <summary>
/// 「動いているはずのものが動いていない」ことを、最後に活動があった時刻からの経過で検出する。
/// </summary>
/// <remarks>
/// <para>
/// 用途は 2 つある。音声は<b>ミキサーの <c>Read</c> が呼ばれること</b>、映像は<b>フレームが提示される
/// こと</b>を活動とみなす。どちらも「例外を伴わずに黙って止まる」経路があり、そこは経過時間でしか
/// 気づけない（音声は <c>WasapiOut.PlaybackStopped</c>、映像は各スレッドの例外記録が拾える範囲の外側）。
/// </para>
/// <para>
/// 判定に使うのは<b>その活動そのものの時刻だけ</b>。似た値で代用しないこと。たとえば音声出力の生死を
/// <c>AudioDecodeThread</c> の充填ゲートの滞留時間で測ろうとすると、一時停止でも同じだけ滞留するため
/// 正常と異常を区別できない（<c>ensemble-review.md</c> §7 の代理値）。
/// </para>
/// <para>
/// 時刻は呼び出し側が渡す。<c>Environment.TickCount64</c> をこのクラスが直接読むと、実時間を
/// 待たずにテストできなくなる。
/// </para>
/// <para>
/// <b>スレッド</b>: <see cref="NoteActivity"/> は活動を起こしている側のスレッド（音声レンダー
/// スレッド・vout スレッド・UI スレッド）から高頻度で呼ばれるため<b>ロックを取らない</b>。
/// 単一フィールドへの <c>Volatile</c> 書き込みで、古い値を読まれても代償は
/// 「報告が 1 度余分に出る／1 度落ちる」にとどまる。
/// <para>
/// <b><see cref="Prime"/> と <see cref="Poll"/> はロックを取る。</b> あの 2 つはフィールド 2 つを
/// <b>対として</b>読み書きするため、別々の <c>Volatile</c> では守れない。実際に起きる綻び:
/// <c>Prime</c> が「最後の活動」を新しくした直後（報告済みの基準はまだ古い）に <c>Poll</c> が
/// 割り込むと、<b>起きていない回復を「もっともらしい滞留時間つきで」報告する</b>。
/// 書き込み順を入れ替えても、こんどは偽の「滞留開始」が出るだけで解決しない。
/// 呼ぶのは UI スレッド・状態タイマー・錨の確定（音声レンダースレッド）で、いずれも低頻度
/// （100ms 周期・操作のたび）なので競合の代償より正しさを採る。
/// </para>
/// </remarks>
public sealed class StallDetector
{
    /// <summary>まだ報告していないことを表す番兵。実際の時刻と衝突しない値を使う。</summary>
    private const long NotReported = long.MinValue;

    /// <summary>
    /// <see cref="Prime"/> と <see cref="Poll"/> がフィールド 2 つを対として扱うためのロック。
    /// <see cref="NoteActivity"/> は取らない（クラスの remarks 参照）。
    /// </summary>
    private readonly object _pairLock = new();

    private readonly int _thresholdMs;
    private long _lastActivityTicks;

    /// <summary>
    /// 報告済みの基準時刻。抑制を「最後の活動の時刻」に紐づけることで、活動が 1 度でも来れば
    /// （基準が動けば）抑制は自動的に解ける。活動側のスレッドから書くフィールドを増やさずに、
    /// <b>回復の検出と滞留時間の実測</b>もこの 1 つの値でできる（<see cref="Poll"/> の remarks）。
    /// </summary>
    private long _reportedForActivityTicks = NotReported;

    /// <param name="thresholdMs">この時間だけ活動が来なければ滞留と判定する。</param>
    public StallDetector(int thresholdMs)
    {
        if (thresholdMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdMs), thresholdMs, "閾値は正の値でなければならない");
        _thresholdMs = thresholdMs;
    }

    /// <summary>活動が起きたときに呼ぶ（音声はミキサーの <c>Read</c> の完了ごと、映像はフレームの提示ごと）。</summary>
    public void NoteActivity(long nowTicks) => Volatile.Write(ref _lastActivityTicks, nowTicks);

    /// <summary>
    /// 基準時刻を置き直し、報告済みの抑制も解除する。活動が来ることを期待し始める時点で呼ぶ
    /// （再生開始・シーク）。<b>報告済みの滞留は回収されずに捨てられる</b>（戻り値を参照）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一時停止中は活動が止まるため、再生へ戻した時点で置き直さないと「止まっていた時間」を
    /// そのまま滞留と判定してしまう。<b>この検出器はアプリ寿命のオブジェクトが持つ</b>ので、
    /// ここで抑制を解除しないと「一度きり」が事実上「再起動するまで二度と」になる
    /// （<c>ensemble-review.md</c> §7 の寿命の食い違い）。
    /// </para>
    /// <para>
    /// <b>既知の副作用</b>: 基準を丸ごと置き直すため、閾値より短い間隔で操作を繰り返すと
    /// （一時停止と再生の連打・連続シーク）滞留が一度も閾値へ達しない。止まっているときに
    /// 操作を連打するのは自然な振る舞いなので、その間は検出が出ないことになる（連打をやめて
    /// 閾値の時間が過ぎれば検出される。<b>見逃しではなく遅れ</b>）。失った時間だけを差し引く形に
    /// すれば連打中も生き残るが、時刻の帳尻合わせを増やすと誤検出——正常な再生を異常と呼ぶ方——の
    /// 危険が上がるため、単純な置き直しを選んでいる。
    /// </para>
    /// </remarks>
    /// <returns>
    /// <b>報告済みの滞留を回収しないまま捨てた場合、その滞留が始まってからの経過 ms。</b>
    /// 捨てるものが無ければ <c>null</c>。捨てると <see cref="StallPhase.Recovered"/> は二度と
    /// 返らないため、記録には滞留の開始だけが残り、<b>回復したのか止まったままなのかが事後に
    /// 判別できない</b>。呼び出し側はこれを見て「打ち切った」ことを記録する責任がある。
    /// <b>戻り値を捨てないこと。</b>
    /// <para>
    /// 経過 ms を返すのは、<b>打ち切りの行が「いつの滞留の後始末か」を自力で示せるようにする</b>ため。
    /// この検出器はエンジンと同じ寿命を持つので、打ち切りは滞留の何時間も後・別のファイルを
    /// 開いた後に起こりうる。値が数秒なら直前の出来事、何時間なら古い滞留の後始末と読める。
    /// </para>
    /// </returns>
    public long? Prime(long nowTicks)
    {
        // 2 つを対として書く。片方だけ見えた状態を Poll に読まれると、起きていない回復を
        // 報告してしまう（クラスの remarks 参照）
        lock (_pairLock)
        {
            long reportedFor = Volatile.Read(ref _reportedForActivityTicks);
            Volatile.Write(ref _lastActivityTicks, nowTicks);
            Volatile.Write(ref _reportedForActivityTicks, NotReported);
            // reportedFor は「滞留の直前に活動があった時刻」なので、差がそのまま滞留の長さになる
            //（滞留の開始を報告した行の ms と同じ尺度で読める）
            return reportedFor == NotReported ? null : nowTicks - reportedFor;
        }
    }

    /// <summary>
    /// 閾値を超えて活動が来ていない状態か。<see cref="Poll"/> と違い、何度呼んでも
    /// 内部状態を変えない（表示側からの問い合わせ用）。活動が戻れば自動的に <c>false</c> へ戻る。
    /// </summary>
    public bool IsStalled(long nowTicks) => IsStalledAt(nowTicks, Volatile.Read(ref _lastActivityTicks));

    /// <summary>
    /// 滞留の判定式。<see cref="IsStalled"/> と <see cref="Poll"/> が同じ述語を使うよう、
    /// 定義はここ 1 箇所に置く（言い換えた瞬間に両者の範囲がずれる）。
    /// </summary>
    private bool IsStalledAt(long nowTicks, long lastActivityTicks) => nowTicks - lastActivityTicks >= _thresholdMs;

    /// <summary>
    /// 滞留の局面を進める。<b>状態を変えるのでポーリング側から 1 周に 1 度だけ呼ぶこと。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>回復も局面として返すのが要点。</b> 滞留の開始しか報告できないと、記録に
    /// 「3012ms 活動が無い」の 1 行だけが残り、<b>3 秒で戻ったのか永久に止まったのかが事後に
    /// 分からない</b>。閾値を実地で調整する材料にもならない。
    /// </para>
    /// <para>
    /// <see cref="StallPhase.Recovered"/> の <c>StalledForMs</c> は<b>実測値</b>。
    /// 報告時の基準（<c>_reportedForActivityTicks</c>）と回復後の最初の活動
    /// （<c>_lastActivityTicks</c>）の差がそのまま「活動が途切れていた時間」になるので、
    /// 時刻を新しく持つ必要はない。
    /// </para>
    /// </remarks>
    public StallPollResult Poll(long nowTicks)
    {
        // 2 つを対として読む。Prime と食い合うと、起きていない回復を報告してしまう
        //（クラスの remarks 参照）。NoteActivity との競合はロック外のままで無害
        lock (_pairLock)
        {
            long lastActivity = Volatile.Read(ref _lastActivityTicks);
            long reportedFor = Volatile.Read(ref _reportedForActivityTicks);

            if (IsStalledAt(nowTicks, lastActivity))
            {
                // 同じ基準で既に報告済みなら継続。基準が動いていれば新しい滞留
                if (reportedFor == lastActivity) return new StallPollResult(StallPhase.Continuing, 0);
                Volatile.Write(ref _reportedForActivityTicks, lastActivity);
                return new StallPollResult(StallPhase.Started, nowTicks - lastActivity);
            }

            // 閾値内。報告済みの滞留があったなら、その基準より後の活動が来ている＝回復
            if (reportedFor != NotReported && reportedFor != lastActivity)
            {
                Volatile.Write(ref _reportedForActivityTicks, NotReported);
                return new StallPollResult(StallPhase.Recovered, lastActivity - reportedFor);
            }
            return new StallPollResult(StallPhase.Running, 0);
        }
    }

    /// <summary>直近の活動からの経過ミリ秒。記録へ添えるための値。</summary>
    public long ElapsedSinceLastActivity(long nowTicks) => nowTicks - Volatile.Read(ref _lastActivityTicks);
}
