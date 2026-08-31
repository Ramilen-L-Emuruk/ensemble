namespace MultiTrackPlayer.Engine.Diagnostics;

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
/// スレッド・vout スレッド・UI スレッド）から高頻度で呼ばれるためロックを取らない。
/// <see cref="Prime"/> と <see cref="ShouldReport"/> はそれぞれ UI スレッド・状態タイマーから
/// 呼ばれる。<b>可視性はフィールド 2 つとも <c>Volatile</c> で揃えてある</b>ので、
/// 残る競合は「どちらの書き込みが先か」だけ。同時に走ると報告が 1 度余分に出る／1 度落ちる
/// 可能性があるが、どちらも記録と案内の重複・欠落にとどまるため許容する。
/// </para>
/// </remarks>
public sealed class StallDetector
{
    /// <summary>まだ報告していないことを表す番兵。実際の時刻と衝突しない値を使う。</summary>
    private const long NotReported = long.MinValue;

    private readonly int _thresholdMs;
    private long _lastActivityTicks;

    /// <summary>
    /// 報告済みの基準時刻。抑制を「最後の活動の時刻」に紐づけることで、活動が 1 度でも来れば
    /// （基準が動けば）抑制は自動的に解ける。ポーリング側が復帰の瞬間を目撃する必要がなく、
    /// 活動側のスレッドから書くフィールドも増えない。
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
    /// （再生開始・シーク）。
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
    public void Prime(long nowTicks)
    {
        Volatile.Write(ref _lastActivityTicks, nowTicks);
        Volatile.Write(ref _reportedForActivityTicks, NotReported);
    }

    /// <summary>
    /// 閾値を超えて活動が来ていない状態か。<see cref="ShouldReport"/> と違い、何度呼んでも
    /// 内部状態を変えない（表示側からの問い合わせ用）。活動が戻れば自動的に <c>false</c> へ戻る。
    /// </summary>
    public bool IsStalled(long nowTicks) => IsStalledAt(nowTicks, Volatile.Read(ref _lastActivityTicks));

    /// <summary>
    /// 滞留の判定式。<see cref="IsStalled"/> と <see cref="ShouldReport"/> が同じ述語を使うよう、
    /// 定義はここ 1 箇所に置く（言い換えた瞬間に両者の範囲がずれる）。
    /// </summary>
    private bool IsStalledAt(long nowTicks, long lastActivityTicks) => nowTicks - lastActivityTicks >= _thresholdMs;

    /// <summary>
    /// 滞留を報告すべきか。閾値を超えている間に <c>true</c> を返すのは 1 度だけで、
    /// 活動が再開すれば次の滞留で改めて <c>true</c> を返す。
    /// </summary>
    public bool ShouldReport(long nowTicks)
    {
        long lastActivity = Volatile.Read(ref _lastActivityTicks);
        if (!IsStalledAt(nowTicks, lastActivity)) return false;
        if (Volatile.Read(ref _reportedForActivityTicks) == lastActivity) return false;
        Volatile.Write(ref _reportedForActivityTicks, lastActivity);
        return true;
    }

    /// <summary>直近の活動からの経過ミリ秒。記録へ添えるための値。</summary>
    public long ElapsedSinceLastActivity(long nowTicks) => nowTicks - Volatile.Read(ref _lastActivityTicks);
}
