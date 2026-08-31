namespace MultiTrackPlayer.Core.Models;

/// <summary>尺をどこから得たか。記録に残して切り分けに使う。</summary>
public enum DurationSource
{
    /// <summary>コンテナの申告値をそのまま使った（通常）。</summary>
    Container,

    /// <summary>コンテナが答えなかったので、ストリーム側の申告値から補完した。</summary>
    Streams,

    /// <summary>どこからも得られなかった。</summary>
    Unknown
}

/// <summary>尺の決定結果。</summary>
/// <param name="Seconds">尺（秒）。<see cref="DurationSource.Unknown"/> のときは 0。</param>
/// <param name="Source">どこから得たか。</param>
public readonly record struct DurationResolution(double Seconds, DurationSource Source)
{
    public bool IsKnown => Source != DurationSource.Unknown;
}

/// <summary>
/// メディアの尺を「コンテナの申告 → ストリームの申告 → 不明」の順で決める。
/// FFmpeg 依存を持たない純ロジックのためユニットテストできる。
/// </summary>
/// <remarks>
/// <para>
/// 判定が必要な理由: コンテナは尺を答えないことがある（ヘッダに尺を持たない形式・ヘッダ無しの VBR・
/// 生ストリーム）。FFmpeg はその場合 <c>AV_NOPTS_VALUE</c> か 0 を返すが、<b>呼び出し側がそれを
/// 検査せずに使うと 2 通りの壊れ方をする</b>。
/// </para>
/// <list type="bullet">
/// <item><c>AV_NOPTS_VALUE</c>（<c>long.MinValue</c>）を秒へ直すと約 -9.2e12 秒になり、
/// <c>TimeSpan.FromSeconds</c> の範囲外で例外になる（＝そのファイルが開けない）</item>
/// <item>0 のまま進むと、尺で割る表示（シークバーのつまみ）が一度も更新されず、
/// 尺で上限を決めるシークの目標も常に 0 になる（＝つまみが動かず、どこを押しても先頭へ戻る）</item>
/// </list>
/// <para>
/// <b>不明を 0 で表さない。</b> 「0 秒の動画」と「尺が分からない動画」は扱いが違うため、
/// <see cref="DurationSource"/> で区別できるようにしてある（呼び出し側は記録の強さを分けられる）。
/// </para>
/// <para>
/// <b>秒はすべて呼び出し側が変換して渡す。</b> <c>AV_NOPTS_VALUE</c> は <see cref="double.NaN"/> へ
/// 直して渡すこと（このクラスは FFmpeg の番兵値を知らない）。
/// </para>
/// </remarks>
public static class MediaDurationResolver
{
    /// <param name="containerSeconds">コンテナ申告の尺（秒）。不明なら <see cref="double.NaN"/>。</param>
    /// <param name="streamSeconds">
    /// 各ストリーム申告の尺（秒）。不明な要素は <see cref="double.NaN"/> でよい。
    /// <b>最長のものを採る</b>——音声だけ、映像だけが尺を持つファイルがあるため。
    /// </param>
    public static DurationResolution Resolve(double containerSeconds, IReadOnlyList<double>? streamSeconds)
    {
        if (IsUsable(containerSeconds)) return new DurationResolution(containerSeconds, DurationSource.Container);

        double longest = 0.0;
        if (streamSeconds != null)
        {
            foreach (double seconds in streamSeconds)
                if (IsUsable(seconds) && seconds > longest) longest = seconds;
        }

        return longest > 0.0
            ? new DurationResolution(longest, DurationSource.Streams)
            : new DurationResolution(0.0, DurationSource.Unknown);
    }

    /// <summary>
    /// <see cref="TimeSpan"/> へ安全に直せる上限。<see cref="TimeSpan.FromSeconds(double)"/> は
    /// 秒をティックへ丸めるため、境界ちょうどの値では丸め上がりで範囲外になりうる。1 秒の余裕を引く。
    /// </summary>
    private static readonly double MaxRepresentableSeconds = TimeSpan.MaxValue.TotalSeconds - 1.0;

    /// <summary>
    /// 尺として使える値か。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0 と負値を弾くのは、どちらも「申告が無い」ことの表現として実際に返ってくるため
    /// （0 秒のメディアは扱う対象にしていない）。
    /// </para>
    /// <para>
    /// <b>上限も弾く。</b> 番兵（<c>AV_NOPTS_VALUE</c>）以外にも、壊れたヘッダは巨大な値を申告しうる。
    /// それを通すと呼び出し側の <c>TimeSpan.FromSeconds</c> が例外になり、
    /// <b>このクラスが防ごうとしている「尺のせいでファイルが開けない」事故に戻る</b>。
    /// なお「1 年を超える動画はおかしい」といった<b>もっともらしさの判定はしない</b>——
    /// 何が妥当かはこのクラスには分からないので、表現できるかどうかだけを見る。
    /// </para>
    /// </remarks>
    private static bool IsUsable(double seconds) =>
        !double.IsNaN(seconds) && !double.IsInfinity(seconds)
        && seconds > 0.0 && seconds <= MaxRepresentableSeconds;
}
