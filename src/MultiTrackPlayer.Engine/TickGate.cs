namespace MultiTrackPlayer.Engine;

/// <summary>スキップの記録を促す種類。<see cref="TickGate.NoteSkip"/> が返す。</summary>
public enum TickSkipReport
{
    /// <summary>記録するものは無い。</summary>
    None,

    /// <summary>
    /// 連続して弾き続けている（1 周が周期を超えたまま終わっていない）。
    /// <b>詰まり 1 つにつき 1 度だけ返る。</b>
    /// </summary>
    Sustained,

    /// <summary>
    /// 累計で弾いた回数が積み上がった。<b>門 1 つにつき 1 度だけ返る。</b>
    /// <para>
    /// <b>いま続いている詰まりについて <see cref="Sustained"/> を返した後は、
    /// その詰まりの間は返らない</b>（同じ 1 つの詰まりを 2 通りの言い方で記録しないため）。
    /// 回復すれば抑制は解ける。
    /// </para>
    /// </summary>
    Intermittent
}

/// <summary>
/// 周期タイマーのコールバックが重複して走らないようにする門。走行中の周があれば後続を弾き、
/// 弾いた回数を数えて「詰まっている」ことを記録させる。
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Threading.Timer</c> は<b>コールバックの完了を待たずに次回発火を予約する</b>ため、
/// 1 周が周期を超えると別のスレッドプールスレッドで重複起動される。単一呼び出しを前提に
/// 書かれた本体（check-then-act や「1 周に 1 度だけ呼ぶ」規約を持つ処理）がそこで壊れる。
/// </para>
/// <para>
/// <b>タイマー 1 つにつき 1 つ作り、そのタイマーのコールバックへ渡すこと。</b>
/// アプリ寿命のオブジェクトのフィールドに持たせると、タイマーを作り直す設計と寿命がずれて
/// <b>2 方向に壊れる</b>。
/// </para>
/// <list type="number">
/// <item>タイマーの停止待ちがタイムアウトして走行中のコールバックが検疫された場合、
/// そのゾンビが「走行中」を握ったままになり、<b>次に作ったタイマーの全周が永久に弾かれる</b></item>
/// <item>逆にゾンビが後から完了すると <see cref="Exit"/> が「走行中」を降ろすため、
/// <b>新しいタイマーの周が走っている最中に排他が破れる</b></item>
/// </list>
/// <para>
/// タイマーと対で作れば、ゾンビは古い門を触るだけになる
/// （<c>ensemble-review.md</c> §7 の「生産側と消費側でオブジェクトの<b>寿命が違う</b>場合、
/// 同じフラグ設計を当てないこと」）。
/// </para>
/// </remarks>
public sealed class TickGate
{
    private readonly int _sustainedSkipThreshold;
    private readonly int _intermittentSkipThreshold;

    private int _running;

    private int _consecutiveSkips;
    private int _sustainedReported;

    private int _totalSkips;
    private int _intermittentReported;

    /// <param name="sustainedSkipThreshold">
    /// <b>連続して</b>弾いた回数がこれに達したとき <see cref="TickSkipReport.Sustained"/> を返す。
    /// 単発の詰まり（GC の一時停止など）と「詰まったまま終わっていない」を区別するための値。
    /// </param>
    /// <param name="intermittentSkipThreshold">
    /// <b>累計で</b>弾いた回数がこれに達したとき <see cref="TickSkipReport.Intermittent"/> を返す。
    /// <b>これが無いと、閾値の手前で回復し続ける詰まりが記録に一切現れない</b>——
    /// 「連続 9 回 → 1 回成功 → 連続 9 回 → …」は永遠に連続の閾値へ届かないのに、
    /// その間ずっと本体は間引かれている。
    /// <para>
    /// ただし、いま続いている詰まりについて <see cref="TickSkipReport.Sustained"/> を
    /// 返した後は、その詰まりの間は返らない（<see cref="NoteSkip"/> の remarks）。
    /// </para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// いずれかが正でない場合、または累計の閾値が連続の閾値より小さい場合。
    /// 後者を許すと累計の側が必ず先に発火し、連続の判定が死ぬ。
    /// </exception>
    public TickGate(int sustainedSkipThreshold, int intermittentSkipThreshold)
    {
        if (sustainedSkipThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(sustainedSkipThreshold), "連続の閾値は正の値であること。");
        if (intermittentSkipThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(intermittentSkipThreshold), "累計の閾値は正の値であること。");
        if (intermittentSkipThreshold < sustainedSkipThreshold)
            throw new ArgumentOutOfRangeException(nameof(intermittentSkipThreshold),
                "累計の閾値は連続の閾値以上であること（下回ると累計側が必ず先に発火し、連続の判定が死ぬ）。");

        _sustainedSkipThreshold = sustainedSkipThreshold;
        _intermittentSkipThreshold = intermittentSkipThreshold;
    }

    /// <summary>門に入る。<b>入れた場合は必ず <see cref="Exit"/> を <c>finally</c> で呼ぶこと。</b></summary>
    /// <returns>入れた場合 true。false なら別のスレッドが走行中。</returns>
    public bool TryEnter() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    /// <summary>門から出る。</summary>
    /// <remarks>
    /// 戻すのは<b>「いま続いている詰まり」に紐づくものだけ</b>——<c>_consecutiveSkips</c>（連続の数え）と
    /// <c>_sustainedReported</c>（その報告の抑制）、それに <c>_running</c>。
    /// <b><c>_totalSkips</c> と <c>_intermittentReported</c> は戻さない。</b>
    /// あちらが測っているのは「この門の間ずっと詰まりがちだった」という別の事実で、
    /// 1 周を終えられたことでは消えない（戻すと、詰まるたびに数えが振り出しへ戻って
    /// 累計の閾値に永久に届かなくなる）。
    /// </remarks>
    public void Exit()
    {
        // 1 周を終えられたなら「続いている詰まり」は解消している。数えと抑制をどちらも戻す
        Volatile.Write(ref _consecutiveSkips, 0);
        Volatile.Write(ref _sustainedReported, 0);
        Volatile.Write(ref _running, 0);
    }

    /// <summary>弾かれた 1 回を数える。</summary>
    /// <param name="consecutiveSkips">これで何回連続して弾いたか。</param>
    /// <param name="totalSkips">この門を作ってから累計で何回弾いたか。</param>
    /// <returns>記録すべき種類。記録するものが無ければ <see cref="TickSkipReport.None"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>連続の抑制は <see cref="Exit"/> で解ける。</b> 「一度きり」を門の寿命に紐づけると、
    /// 一度詰まって回復した後の 2 度目の詰まりが報告されない。抑制を「いま続いている詰まり」に
    /// 紐づけることで、回復すれば自動的に次を報告できる。報告には連続の閾値ぶんの回数が要るので、
    /// 境界を跨いで振動しても記録が溢れることはない。
    /// </para>
    /// <para>
    /// <b>累計の抑制は解かない。</b> 門 1 つにつき 1 度で足りる——
    /// 「断続的に詰まりがち」は同じ門の中で何度も言う意味が無い事実だから。
    /// 門はタイマーごとに作り直されるので「再起動するまで二度と」にはならない。
    /// </para>
    /// <para>
    /// <b>いま続いている詰まりについて <see cref="TickSkipReport.Sustained"/> を返した後は、
    /// その詰まりの間 <see cref="TickSkipReport.Intermittent"/> を返さない。</b>
    /// 2 つのカウンタは独立なので、<see cref="Exit"/> を挟まない 1 つの詰まりでは連続が累計の
    /// 閾値も超えて<b>両方が発火しうる</b>。同じ詰まりを 2 通りの言い方で記録しても読み手が
    /// 混乱するだけなので、重い方（連続）だけを残す。
    /// </para>
    /// <para>
    /// <b>この抑制を門の寿命へ広げないこと。</b> <c>Exit</c> で解けない形にすると、
    /// 一度長く詰まっただけで<b>以後そのタイマーの残り時間ずっと断続の検出が死ぬ</b>——
    /// しかも死んだことの痕跡も出ない。抑制は「いま続いている詰まり」に紐づける
    /// （このクラス自身の remarks に書いた寿命の話が、そのままここにも当てはまる）。
    /// </para>
    /// <para>
    /// <b>同じ詰まりで 2 行出ることはある。競合ではなく、決定的に起きる。</b>
    /// 抑制は <c>Sustained</c> → <c>Intermittent</c> の一方向だけで、逆向きは見ていない。
    /// 2 つの数えは独立で、累計は <see cref="Exit"/> で戻らないため、次の順で両方が発火する
    /// （閾値が連続 10・累計 50 の場合の実数値）。
    /// </para>
    /// <list type="number">
    /// <item>連続 9 回で回復する詰まりを 5 周——累計 45。連続は 10 に届かないので何も出ない</item>
    /// <item>新しい詰まりが始まり<b>連続 5 回目</b>で累計が 50 に達する →
    /// <c>Intermittent</c>（この時点で <c>_sustainedReported</c> は 0 なので通る）</item>
    /// <item>同じ詰まりのまま<b>連続 10 回目</b> → <c>Sustained</c></item>
    /// </list>
    /// <para>
    /// <b>これは仕様として受け入れている。</b> 2 行の内容はどちらも事実で矛盾せず
    /// （文面から「連続では閾値に届いていない」という主張を外してある）、
    /// <b>「断続的だったものが長い詰まりへ悪化した」という経緯として読める</b>。
    /// 逆向きの抑制を足すと、<c>Intermittent</c> を報告済みの門で <c>Sustained</c> が
    /// 出なくなる——より重い事実を落とすので採らない。2 行を 1 行へ畳むのも、
    /// 発火の時点が違うものを無理に合わせることになる。
    /// </para>
    /// <para>
    /// <b>読み手が「別々の 2 つの障害」と数え違えないのは、2 行が語る時間の性質が違うから。</b>
    /// <c>Sustained</c> の行は「いま超えたまま終わっていない」——現在進行の 1 つの詰まり。
    /// <c>Intermittent</c> の行は「頻発している」——期間全体の傾向で、累計の範囲も
    /// 「このタイマーで」と明示してある。<b>並べれば同じ事象の 2 面だと読める。</b>
    /// 文面を変えるときはこの対比を壊さないこと（どちらも「N 回」を含むので、
    /// 現在進行と傾向の区別が文面から消えると数え違えるようになる）。
    /// </para>
    /// <para>
    /// 数えは目安。<see cref="TryEnter"/> が失敗してからここへ来る間に走行中の周が終われば、
    /// 解消済みの分を数えることがある。閾値に対して数回のずれは判断を変えない。
    /// </para>
    /// </remarks>
    public TickSkipReport NoteSkip(out int consecutiveSkips, out int totalSkips)
    {
        consecutiveSkips = Interlocked.Increment(ref _consecutiveSkips);
        totalSkips = Interlocked.Increment(ref _totalSkips);

        if (consecutiveSkips >= _sustainedSkipThreshold
            && Interlocked.CompareExchange(ref _sustainedReported, 1, 0) == 0)
            return TickSkipReport.Sustained;

        // いま続いている詰まりを連続として報告済みなら、同じ詰まりを累計の話で重ねない。
        // **見るのは Exit で戻る側のフラグ。** 戻らないフラグにすると検出が永久に死ぬ（remarks 参照）
        if (Volatile.Read(ref _sustainedReported) == 0
            && totalSkips >= _intermittentSkipThreshold
            && Interlocked.CompareExchange(ref _intermittentReported, 1, 0) == 0)
            return TickSkipReport.Intermittent;

        return TickSkipReport.None;
    }
}
