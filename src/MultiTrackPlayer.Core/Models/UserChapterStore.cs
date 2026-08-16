using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTrackPlayer.Core.Models;

/// <summary>
/// ユーザーが手で追加したチャプターを動画ファイルごとに %APPDATA% へ保存する。
/// 保存先は動画のフルパスから導いたハッシュ名の JSON 1 ファイル。
/// 書き込みは一時ファイル経由で差し替えるため、途中で異常終了しても既存のファイルは壊れない。
/// </summary>
public class UserChapterStore
{
    private static readonly string StorageDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MultiTrackPlayer", "chapters");

    /// <summary>チャプタータイトルとして保存を許す最大文字数。</summary>
    private const int MaxTitleLength = 500;

    /// <param name="error">
    /// 読み込めなかった場合にその理由が入る。読み込めた場合・保存ファイルが無い場合は null。
    /// このプロジェクトは依存を持たないためロガーを呼べない。理由は呼び出し側で記録すること。
    /// </param>
    public static IReadOnlyList<ChapterInfo> Load(string filePath, int existingCount, out string? error)
    {
        error = null;
        var path = ResolveExistingPath(filePath);
        if (path == null) return Array.Empty<ChapterInfo>();

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<List<UserChapterEntry>>(json)
                          ?? new List<UserChapterEntry>();

            return entries
                // startTimeMs が欠落・型不一致だと System.Text.Json は既定値 0 を割り当てて完走するため、
                // 「先頭に積み上がった複数チャプター」として現れる。負値ともども取り除く
                .Where(e => e.StartTimeMs >= 0)
                .Select((e, i) => new ChapterInfo(
                    existingCount + i,
                    Truncate(e.Title) ?? $"Chapter {existingCount + i + 1}",
                    TimeSpan.FromMilliseconds(e.StartTimeMs),
                    IsUserDefined: true))
                .ToList();
        }
        catch (Exception ex)
        {
            // 壊れたファイルをそのまま残すと、次にユーザーが 1 件追加して保存した時点で
            // ファイル全体が上書きされ、元の内容を永久に失う。退避してから空として扱う
            error = $"{path}: {ex.Message}";
            TryBackupUnreadableFile(path);
            return Array.Empty<ChapterInfo>();
        }
    }

    /// <summary>チャプターを保存する。</summary>
    /// <param name="error">保存できなかった場合にその理由が入る。成功時は null。</param>
    /// <returns>保存できた場合 true。失敗した場合 false（既存のファイルは保持される）。</returns>
    public static bool Save(string filePath, IEnumerable<ChapterInfo> userChapters, out string? error)
    {
        error = null;
        var entries = userChapters
            .Where(c => c.IsUserDefined)
            .OrderBy(c => c.StartTime)
            .Select(c => new UserChapterEntry
            {
                Title = Truncate(c.Title),
                StartTimeMs = (long)c.StartTime.TotalMilliseconds
            })
            .ToList();

        var path = GetJsonPath(filePath);
        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(StorageDir);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            // 直接上書きすると、書き込み途中の強制終了・ディスクフルで空や壊れた JSON が残る。
            // 一時ファイルへ書き切ってから差し替えることで、既存ファイルは常に読める状態を保つ
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
            // 正規化前のハッシュで作られた旧ファイルが残っていれば、この時点で不要になる
            var legacyPath = GetJsonPathForKey(filePath);
            if (legacyPath != path) TryDelete(legacyPath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{path}: {ex.Message}";
            TryDelete(tempPath);
            return false;
        }
    }

    /// <summary>読み込むべき既存ファイルのパスを返す。無ければ null。</summary>
    private static string? ResolveExistingPath(string filePath)
    {
        var path = GetJsonPath(filePath);
        if (File.Exists(path)) return path;

        // パス表記の正規化を導入する前のバージョンで保存されたファイルがあれば引き継ぐ
        var legacyPath = GetJsonPathForKey(filePath);
        if (legacyPath == path || !File.Exists(legacyPath)) return null;

        try
        {
            File.Move(legacyPath, path);
            return path;
        }
        catch
        {
            // 別プロセス／別スレッドが先に移行を終えていた場合は、そちらの結果を使う。
            // 存在しない legacyPath を返すと「読み込み失敗」と誤って記録されてしまう
            return File.Exists(path) ? path : legacyPath;
        }
    }

    private static string GetJsonPath(string filePath) => GetJsonPathForKey(NormalizeKey(filePath));

    /// <summary>
    /// 同じ動画を指していても、相対/絶対・大文字小文字の違いで別ファイル扱いになるのを防ぐ。
    /// （D&D・コマンドライン引数・IPC で表記が変わりうる）
    /// </summary>
    private static string NormalizeKey(string filePath)
    {
        try { return Path.GetFullPath(filePath).ToLowerInvariant(); }
        catch { return filePath.ToLowerInvariant(); }
    }

    private static string GetJsonPathForKey(string key)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToLower();
        return Path.Combine(StorageDir, $"{hash}.json");
    }

    private static string? Truncate(string? title)
        => title != null && title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;

    private static void TryBackupUnreadableFile(string path)
    {
        try { File.Move(path, path + ".bak", overwrite: true); }
        catch { /* 退避できなくても再生機能は止めない */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 一時ファイルが残っても次回の保存で上書きされる */ }
    }

    private class UserChapterEntry
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("startTimeMs")]
        public long StartTimeMs { get; set; }
    }
}
