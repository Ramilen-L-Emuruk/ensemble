using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTrackPlayer.Core.Models;

/// <summary>
/// シークバーサムネイルのスプライトシートを %APPDATA%\MultiTrackPlayer\thumbnails に永続化する。
/// キーはファイルパスのMD5（UserChapterStore と同じ命名規則）。
/// 合計サイズが上限を超えたら、最終アクセスが古いものから削除する（LRU）。
/// </summary>
public static class ThumbnailCacheStore
{
    private const long MaxTotalCacheBytes = 500L * 1024 * 1024;

    private static readonly string StorageDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MultiTrackPlayer", "thumbnails");

    public static (string JpgPath, string JsonPath) GetCachePaths(string filePath)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(filePath))).ToLower();
        return (Path.Combine(StorageDir, $"{hash}.jpg"), Path.Combine(StorageDir, $"{hash}.json"));
    }

    public static void EnsureStorageDir() => Directory.CreateDirectory(StorageDir);

    public static ThumbnailSheet? Load(string filePath)
    {
        var (jpgPath, jsonPath) = GetCachePaths(filePath);
        if (!File.Exists(jpgPath) || !File.Exists(jsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            var entry = JsonSerializer.Deserialize<ThumbnailSheetEntry>(json);
            if (entry == null) return null;

            Touch(jpgPath, jsonPath);
            return new ThumbnailSheet(jpgPath, entry.Columns, entry.Rows, entry.TileWidth,
                entry.TileHeight, entry.Count, entry.SampleIntervalSeconds, entry.SourceDurationSeconds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ジェネレーターが JPEG 本体を書き終えた後、インデックスJSONを保存してキャッシュ上限を適用する。</summary>
    public static void SaveIndex(ThumbnailSheet sheet)
    {
        EnsureStorageDir();
        var jsonPath = Path.ChangeExtension(sheet.SheetPath, ".json");
        var entry = new ThumbnailSheetEntry
        {
            Columns = sheet.Columns,
            Rows = sheet.Rows,
            TileWidth = sheet.TileWidth,
            TileHeight = sheet.TileHeight,
            Count = sheet.Count,
            SampleIntervalSeconds = sheet.SampleIntervalSeconds,
            SourceDurationSeconds = sheet.SourceDurationSeconds
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(entry), Encoding.UTF8);

        EvictIfOverBudget();
    }

    private static void Touch(string jpgPath, string jsonPath)
    {
        try
        {
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(jpgPath, now);
            File.SetLastWriteTimeUtc(jsonPath, now);
        }
        catch
        {
            // 最終アクセス更新の失敗はLRU精度が落ちるだけで致命的ではないため無視する
        }
    }

    private static void EvictIfOverBudget()
    {
        if (!Directory.Exists(StorageDir)) return;

        var jpgFiles = new DirectoryInfo(StorageDir).GetFiles("*.jpg");
        long total = jpgFiles.Sum(f => f.Length);
        if (total <= MaxTotalCacheBytes) return;

        foreach (var file in jpgFiles.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= MaxTotalCacheBytes) break;
            var jsonPath = Path.ChangeExtension(file.FullName, ".json");
            try
            {
                total -= file.Length;
                file.Delete();
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
            }
            catch
            {
                // 削除に失敗した1件はスキップして次の候補へ進む
            }
        }
    }

    private class ThumbnailSheetEntry
    {
        [JsonPropertyName("columns")]
        public int Columns { get; set; }
        [JsonPropertyName("rows")]
        public int Rows { get; set; }
        [JsonPropertyName("tileWidth")]
        public int TileWidth { get; set; }
        [JsonPropertyName("tileHeight")]
        public int TileHeight { get; set; }
        [JsonPropertyName("count")]
        public int Count { get; set; }
        [JsonPropertyName("sampleIntervalSeconds")]
        public double SampleIntervalSeconds { get; set; }
        [JsonPropertyName("sourceDurationSeconds")]
        public double SourceDurationSeconds { get; set; }
    }
}
