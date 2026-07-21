namespace MultiTrackPlayer.Core.Models;

/// <summary>
/// シークバーのホバー時サムネイル用スプライトシートのメタデータ。
/// タイルは行優先（左から右、上から下）で SampleIntervalSeconds ごとに敷き詰められている。
/// </summary>
public record ThumbnailSheet(
    string SheetPath,
    int Columns,
    int Rows,
    int TileWidth,
    int TileHeight,
    int Count,
    double SampleIntervalSeconds,
    double SourceDurationSeconds,
    int Version = 0)
{
    /// <summary>生成完了前の途中経過か（true の間はグレーの未生成タイルが混ざっている）。</summary>
    public bool IsComplete { get; init; }


    /// <summary>指定位置に最も近いタイルの、シート内ピクセル矩形を返す。</summary>
    public (int X, int Y, int Width, int Height) GetTileRect(TimeSpan position)
    {
        int index = SampleIntervalSeconds > 0
            ? (int)(position.TotalSeconds / SampleIntervalSeconds)
            : 0;
        index = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        int col = index % Columns;
        int row = index / Columns;
        return (col * TileWidth, row * TileHeight, TileWidth, TileHeight);
    }
}
