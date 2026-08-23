namespace MultiTrackPlayer.Engine;

/// <summary>
/// シーク世代。1 回のシーク要求に対して 1 つだけ採番され、そのシークで生まれたデータすべて
/// （パケットキューの Flush 番兵・パケット・映像リングのスロット）に刻まれる。
///
/// <para>
/// <b>採番できるのは <see cref="Pipeline.DemuxThread.RequestSeek"/> だけ</b>。他のどのコンポーネントも
/// 世代を自分で進めてはならない。受け取った世代をそのまま下流へ渡し、判定は<b>等値</b>で行う。
/// </para>
/// <para>
/// この型が存在する理由: 以前はパケットキュー・映像リングがそれぞれ独立に「Flush のたびに +1」する
/// カウンタを持っていた。映像リングの Flush は 1 シークにつき 2 回呼ばれるため、キューの世代が 1 進む
/// 間にリングの世代は 2 進み、消費側は「次の世代 = 現在 + 1」と<b>予測</b>するしかなかった。
/// 予測は別スレッドの副作用の回数に依存するため、Flush の呼び出し箇所が 1 つ増えるだけで静かに外れ、
/// シーク目標を引けずプリロールが完了しないまま固まる（<c>.claude/rules/ensemble-review.md</c> §1）。
/// 世代をデータに刻んで持ち回れば、予測は不要になり比較は等値だけで済む。
/// </para>
/// <para>
/// <see cref="MediaEngine"/> が持つ他の世代（パイプライン実体の世代・vout スレッドの世代）とは
/// 寿命も目的も異なる別物。素の <c>int</c> のままだと取り違えてもコンパイルが通ってしまうため、
/// 専用の型にして誤用をコンパイルエラーにしている。
/// </para>
/// <para>
/// 世代は<b>連番であることを保証しない</b>。シーク要求はコアレスされる（保留中の要求は最新の 1 件に
/// 上書きされる）ため、採番されたまま実際のシークに使われない世代が飛び番として残りうる。
/// 必要な性質は「単調増加」と「一意」だけ。
/// </para>
/// </summary>
/// <param name="Value">世代番号。ファイルを開いた直後は 0。</param>
public readonly record struct SeekEpoch(int Value) : IComparable<SeekEpoch>
{
    /// <summary>ファイルを開いた直後（まだ一度もシークしていない）の世代。</summary>
    public static SeekEpoch Initial => new(0);

    /// <summary>
    /// 次の世代。<see cref="Pipeline.DemuxThread.RequestSeek"/> 以外から呼んではならない。
    /// <c>internal</c> にしてあるのは UI 層からの誤用を防ぐため。ただしパイプライン全体が
    /// Engine アセンブリ内にあるので、これで契約が強制されるわけではない
    /// （実質の担保は <c>.claude/rules/ensemble-review.md</c> §6 のチェック項目）。
    /// </summary>
    internal SeekEpoch Next() => new(Value + 1);

    public int CompareTo(SeekEpoch other) => Value.CompareTo(other.Value);

    public static bool operator <(SeekEpoch left, SeekEpoch right) => left.Value < right.Value;
    public static bool operator <=(SeekEpoch left, SeekEpoch right) => left.Value <= right.Value;
    public static bool operator >(SeekEpoch left, SeekEpoch right) => left.Value > right.Value;
    public static bool operator >=(SeekEpoch left, SeekEpoch right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString();
}
