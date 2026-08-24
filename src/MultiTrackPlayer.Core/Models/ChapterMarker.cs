namespace MultiTrackPlayer.Core.Models;

/// <summary>チャプターの位置をシークバー上の比率（0.0〜1.0）で表したもの。</summary>
/// <param name="Ratio">シークバーの左端を 0.0、右端を 1.0 とした位置。</param>
/// <param name="Title">マーカーに表示するチャプター名。</param>
public readonly record struct ChapterMarker(double Ratio, string Title);

/// <summary>
/// チャプターの時刻から、シークバーに描くマーカーの位置を求める。
/// </summary>
/// <remarks>
/// 通常時とフルスクリーンの 2 つのシークバーが同じ結果を使うため、算出はここに 1 つだけ置く。
/// 表示側（<c>SeekBarControl</c>）は受け取った比率をそのまま座標へ写すだけで、範囲の判断はしない。
/// </remarks>
public static class ChapterMarkers
{
    /// <summary>マーカーを組み立てる。</summary>
    /// <param name="chapters">チャプターの開始時刻と名前。</param>
    /// <param name="duration">メディアの尺。0 以下なら比率を決められないので空を返す。</param>
    /// <returns>入力と同じ順序のマーカー。比率は 0.0〜1.0 に収める。</returns>
    /// <remarks>
    /// 比率を丸めるのは、尺の外にあるチャプターがバーの外へ描かれるのを防ぐため。
    /// 壊れたメタデータや、尺の取得に失敗して実際より短い値を持っているファイルで起こりうる。
    /// </remarks>
    public static IReadOnlyList<ChapterMarker> Build(
        IEnumerable<(TimeSpan Start, string Title)> chapters, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return Array.Empty<ChapterMarker>();

        var markers = new List<ChapterMarker>();
        foreach (var (start, title) in chapters)
        {
            double ratio = start.TotalSeconds / duration.TotalSeconds;
            markers.Add(new ChapterMarker(Math.Clamp(ratio, 0.0, 1.0), title));
        }
        return markers;
    }
}
