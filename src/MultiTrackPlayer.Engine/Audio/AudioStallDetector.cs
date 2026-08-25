namespace MultiTrackPlayer.Engine.Audio;

/// <summary>
/// ミキサーの <c>Read</c> が呼ばれなくなったこと（＝音声出力が動いていないこと）を経過時間で検出する。
/// </summary>
/// <remarks>
/// <para>
/// 例外を伴う異常停止は <c>WasapiOut.PlaybackStopped</c> が申告するが、例外を出さずに <c>Read</c> が
/// 止まる経路（デバイスが応答しなくなる等）はどこにも現れない。<c>Read</c> が止まると音が消えるだけでなく
/// audio-master クロックが進まなくなるため、位置表示と映像まで止まる。
/// </para>
/// <para>
/// 判定に使うのは「最後に <c>Read</c> が呼ばれた時刻」だけ。<c>AudioDecodeThread</c> の充填ゲートの
/// 滞留時間では代用できない（一時停止でも同じだけ滞留するため、正常と異常を区別できない）。
/// </para>
/// <para>
/// 時刻は呼び出し側が渡す。<c>Environment.TickCount64</c> をこのクラスが直接読むと、実時間を
/// 待たずにテストできなくなる。
/// </para>
/// <para>
/// <b>スレッド</b>: <see cref="NoteRead"/> は音声レンダースレッドから毎バッファ呼ばれるため
/// ロックを取らない（音声スレッドを他スレッドの都合で待たせると出力が途切れる）。
/// <see cref="Prime"/> と <see cref="ShouldReport"/> はそれぞれ UI スレッド・状態タイマーから
/// 呼ばれる。<b>可視性はフィールド 2 つとも <c>Volatile</c> で揃えてある</b>ので、
/// 残る競合は「どちらの書き込みが先か」だけ。同時に走ると報告が 1 度余分に出る／1 度落ちる
/// 可能性があるが、どちらも記録と案内の重複・欠落にとどまるため許容する。
/// </para>
/// </remarks>
public sealed class AudioStallDetector
{
    /// <summary>まだ報告していないことを表す番兵。実際の時刻と衝突しない値を使う。</summary>
    private const long NotReported = long.MinValue;

    private readonly int _thresholdMs;
    private long _lastReadTicks;

    /// <summary>
    /// 報告済みの基準時刻。抑制を「最後の <c>Read</c> の時刻」に紐づけることで、<c>Read</c> が
    /// 1 度でも来れば（基準が動けば）抑制は自動的に解ける。ポーリング側が復帰の瞬間を
    /// 目撃する必要がなく、音声レンダースレッドから書くフィールドも増えない。
    /// </summary>
    private long _reportedForReadTicks = NotReported;

    /// <param name="thresholdMs">この時間だけ <c>Read</c> が来なければ滞留と判定する。</param>
    public AudioStallDetector(int thresholdMs)
    {
        if (thresholdMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdMs), thresholdMs, "閾値は正の値でなければならない");
        _thresholdMs = thresholdMs;
    }

    /// <summary><c>Read</c> の完了ごとに呼ぶ。</summary>
    public void NoteRead(long nowTicks) => Volatile.Write(ref _lastReadTicks, nowTicks);

    /// <summary>
    /// 基準時刻を置き直し、報告済みの抑制も解除する。<c>Read</c> が来ることを期待し始める時点で呼ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一時停止中は <c>Read</c> が止まるため、再生へ戻した時点で置き直さないと「止まっていた時間」を
    /// そのまま滞留と判定してしまう。<b>この検出器はアプリ寿命のオブジェクトが持つ</b>ので、
    /// ここで抑制を解除しないと「一度きり」が事実上「再起動するまで二度と」になる
    /// （<c>ensemble-review.md</c> §7 の寿命の食い違い）。
    /// </para>
    /// <para>
    /// <b>既知の副作用</b>: 基準を丸ごと置き直すため、閾値より短い間隔で一時停止と再生を繰り返すと
    /// 滞留が一度も閾値へ達しない。音が出ないときに再生ボタンを連打するのは自然な操作なので、
    /// その間は検出が出ないことになる（連打をやめて閾値の時間が過ぎれば検出される。
    /// <b>見逃しではなく遅れ</b>）。一時停止で失った時間だけを差し引く形にすれば連打中も生き残るが、
    /// 時刻の帳尻合わせを増やすと誤検出——正常な再生を異常と呼ぶ方——の危険が上がるため、
    /// 単純な置き直しを選んでいる。
    /// </para>
    /// </remarks>
    public void Prime(long nowTicks)
    {
        Volatile.Write(ref _lastReadTicks, nowTicks);
        Volatile.Write(ref _reportedForReadTicks, NotReported);
    }

    /// <summary>
    /// 閾値を超えて <c>Read</c> が来ていない状態か。<see cref="ShouldReport"/> と違い、何度呼んでも
    /// 内部状態を変えない（表示側からの問い合わせ用）。<c>Read</c> が戻れば自動的に <c>false</c> へ戻る。
    /// </summary>
    public bool IsStalled(long nowTicks) => IsStalledAt(nowTicks, Volatile.Read(ref _lastReadTicks));

    /// <summary>
    /// 滞留の判定式。<see cref="IsStalled"/> と <see cref="ShouldReport"/> が同じ述語を使うよう、
    /// 定義はここ 1 箇所に置く（言い換えた瞬間に両者の範囲がずれる）。
    /// </summary>
    private bool IsStalledAt(long nowTicks, long lastReadTicks) => nowTicks - lastReadTicks >= _thresholdMs;

    /// <summary>
    /// 滞留を報告すべきか。閾値を超えている間に <c>true</c> を返すのは 1 度だけで、
    /// <c>Read</c> が再開すれば次の滞留で改めて <c>true</c> を返す。
    /// </summary>
    public bool ShouldReport(long nowTicks)
    {
        long lastRead = Volatile.Read(ref _lastReadTicks);
        if (!IsStalledAt(nowTicks, lastRead)) return false;
        if (Volatile.Read(ref _reportedForReadTicks) == lastRead) return false;
        Volatile.Write(ref _reportedForReadTicks, lastRead);
        return true;
    }

    /// <summary>直近の <c>Read</c> からの経過ミリ秒。記録へ添えるための値。</summary>
    public long ElapsedSinceLastRead(long nowTicks) => nowTicks - Volatile.Read(ref _lastReadTicks);
}
