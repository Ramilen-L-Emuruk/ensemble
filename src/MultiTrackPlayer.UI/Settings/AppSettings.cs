using System.IO;
using System.Text.Json;

namespace MultiTrackPlayer.UI.Settings;

/// <summary>
/// アプリ全般の設定。%APPDATA%\MultiTrackPlayer\settings.json に永続化する。
/// </summary>
public class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MultiTrackPlayer", "settings.json");

    /// <summary>デバッグモード: ステータスバー（ドロップ統計）表示 + 診断ログ書き出しを有効化する。</summary>
    public bool DebugMode { get; set; }

    /// <summary>
    /// ファイルを開いたとき既定でミュートするトラック番号（1始まり）を、ファイルが置かれたディレクトリごとに保持する。
    /// キーはディレクトリの絶対パス（大文字小文字を区別しない）。
    /// 例: あるフォルダで 1 が Main Mix・2 以降が個別音源の録画なら [2,3,4,...] を入れて Main Mix だけ聴く。
    /// </summary>
    public Dictionary<string, List<int>> DefaultMutedTracksByDirectory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    // System.Text.Json はデシリアライズ時に辞書の比較子（OrdinalIgnoreCase）を保持しないため作り直す
                    loaded.DefaultMutedTracksByDirectory =
                        new Dictionary<string, List<int>>(loaded.DefaultMutedTracksByDirectory, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // 壊れた設定ファイルは既定値で上書き起動する（起動不能にしない）
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失敗は致命的ではないため握りつぶす（次回起動時は旧設定のまま）
        }
    }
}
