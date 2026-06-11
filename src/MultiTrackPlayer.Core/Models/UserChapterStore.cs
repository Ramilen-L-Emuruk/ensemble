using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTrackPlayer.Core.Models;

public class UserChapterStore
{
    private static readonly string StorageDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MultiTrackPlayer", "chapters");

    public static IReadOnlyList<ChapterInfo> Load(string filePath, int existingCount)
    {
        var path = GetJsonPath(filePath);
        if (!File.Exists(path)) return Array.Empty<ChapterInfo>();

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<List<UserChapterEntry>>(json)
                          ?? new List<UserChapterEntry>();

            return entries.Select((e, i) => new ChapterInfo(
                existingCount + i,
                e.Title ?? $"Chapter {existingCount + i + 1}",
                TimeSpan.FromMilliseconds(e.StartTimeMs),
                IsUserDefined: true)).ToList();
        }
        catch
        {
            return Array.Empty<ChapterInfo>();
        }
    }

    public static void Save(string filePath, IEnumerable<ChapterInfo> userChapters)
    {
        Directory.CreateDirectory(StorageDir);
        var entries = userChapters
            .Where(c => c.IsUserDefined)
            .OrderBy(c => c.StartTime)
            .Select(c => new UserChapterEntry { Title = c.Title, StartTimeMs = (long)c.StartTime.TotalMilliseconds })
            .ToList();

        var path = GetJsonPath(filePath);
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string GetJsonPath(string filePath)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(filePath))).ToLower();
        return Path.Combine(StorageDir, $"{hash}.json");
    }

    private class UserChapterEntry
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("startTimeMs")]
        public long StartTimeMs { get; set; }
    }
}
