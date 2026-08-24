using MultiTrackPlayer.Core.Models;
using Xunit;

namespace MultiTrackPlayer.Tests.Chapters;

/// <summary>
/// <see cref="ChapterMarkers"/> の位置決めを検証する。表示側は受け取った比率をそのまま座標へ
/// 写すだけなので、範囲外の値を出さないことがここの責任になる。
/// </summary>
public sealed class ChapterMarkersTests
{
    private static (TimeSpan Start, string Title) Chapter(double seconds, string title = "ch")
        => (TimeSpan.FromSeconds(seconds), title);

    [Fact(DisplayName = "尺に対する比率を返す")]
    public void Build_ReturnsRatioAgainstDuration()
    {
        var markers = ChapterMarkers.Build(
            new[] { Chapter(0), Chapter(30), Chapter(90) }, TimeSpan.FromSeconds(120));

        Assert.Equal(new[] { 0.0, 0.25, 0.75 }, markers.Select(m => m.Ratio));
    }

    [Fact(DisplayName = "入力の順序とタイトルを保つ")]
    public void Build_KeepsOrderAndTitles()
    {
        var markers = ChapterMarkers.Build(
            new[] { Chapter(60, "後半"), Chapter(10, "冒頭") }, TimeSpan.FromSeconds(120));

        Assert.Equal(new[] { "後半", "冒頭" }, markers.Select(m => m.Title));
        Assert.Equal(0.5, markers[0].Ratio);
    }

    [Fact(DisplayName = "尺が未確定なら空を返す")]
    public void Build_ReturnsEmpty_WhenDurationIsZero()
    {
        var markers = ChapterMarkers.Build(new[] { Chapter(10) }, TimeSpan.Zero);

        Assert.Empty(markers);
    }

    [Fact(DisplayName = "尺が負なら空を返す")]
    public void Build_ReturnsEmpty_WhenDurationIsNegative()
    {
        var markers = ChapterMarkers.Build(new[] { Chapter(10) }, TimeSpan.FromSeconds(-5));

        Assert.Empty(markers);
    }

    [Fact(DisplayName = "チャプターが無ければ空を返す")]
    public void Build_ReturnsEmpty_WhenNoChapters()
    {
        var markers = ChapterMarkers.Build(Array.Empty<(TimeSpan, string)>(), TimeSpan.FromSeconds(120));

        Assert.Empty(markers);
    }

    // 壊れたメタデータや、尺を実際より短く取得したファイルで起こりうる。
    // 丸めないとマーカーがバーの外へ描かれる
    [Fact(DisplayName = "尺より後のチャプターは右端に収める")]
    public void Build_ClampsRatio_WhenChapterIsBeyondDuration()
    {
        var markers = ChapterMarkers.Build(
            new[] { Chapter(180), Chapter(120) }, TimeSpan.FromSeconds(120));

        Assert.Equal(new[] { 1.0, 1.0 }, markers.Select(m => m.Ratio));
    }

    [Fact(DisplayName = "負の開始時刻は左端に収める")]
    public void Build_ClampsRatio_WhenStartIsNegative()
    {
        var markers = ChapterMarkers.Build(new[] { Chapter(-10) }, TimeSpan.FromSeconds(120));

        Assert.Equal(0.0, markers[0].Ratio);
    }
}
