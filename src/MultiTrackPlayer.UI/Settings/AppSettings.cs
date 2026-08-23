using System.IO;
using System.Text.Json;
using MultiTrackPlayer.Engine.Diagnostics;

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

    /// <summary>
    /// 設定ファイルを読めず既定値で起動したか（ファイルが無い初回起動は含まない）。
    /// 記録だけでは利用者が「設定が勝手に初期化された」ことに気づけないため、起動後に知らせる。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WasRestoredToDefaults { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded == null)
                {
                    // 中身が JSON の null だと例外は飛ばない。ここを記録しないと
                    // 「壊れた設定は必ず記録する」が 1 経路だけ抜ける
                    DiagnosticLog.WriteFatal("settings", "設定ファイルの中身が空のため既定値で起動する");
                    return new AppSettings { WasRestoredToDefaults = true };
                }

                // System.Text.Json はデシリアライズ時に辞書の比較子（OrdinalIgnoreCase）を保持しないため作り直す
                loaded.DefaultMutedTracksByDirectory =
                    new Dictionary<string, List<int>>(loaded.DefaultMutedTracksByDirectory, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception ex)
        {
            // 壊れた設定ファイルは既定値で上書き起動する（起動不能にしない）。
            // ただし記録は残し、利用者にも知らせられるよう印を付ける。無言だと
            // 「設定が勝手に初期化された」ことを後から確かめられない。
            // 診断ログは既定で無効なので、必ず残る側へ書く（この時点ではまだ有効化もされていない）
            DiagnosticLog.WriteFatal("settings", $"設定を読み込めなかったため既定値で起動する: {ex}");
            return new AppSettings { WasRestoredToDefaults = true };
        }
        // ここへ来るのはファイルが無い初回起動だけ。異常ではないので印は付けない
        return new AppSettings();
    }

    /// <summary>設定を保存する。失敗しても例外は投げない（呼び出し側は戻り値で判断する）。</summary>
    /// <returns>保存できた場合 true。</returns>
    public bool Save()
    {
        var tempPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 直接上書きすると、書き込み途中の強制終了・ディスクフルで空や壊れた JSON が残る。
            // 壊れた設定は次の起動で既定値へ差し替わるため、1 回の保存失敗が全設定の消失に化ける。
            // 一時ファイルへ書き切ってから差し替える（UserChapterStore.Save と同じ手当て）
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // 握り潰すと「保存したのに次回起動で戻っている」を追う手がかりが無くなる。
            // アプリは動き続けられるので例外は投げず、記録と戻り値で伝える
            DiagnosticLog.WriteFatal("settings", $"設定を保存できなかった（次回起動時は旧設定のまま）: {ex}");
            // 差し替えの手前で失敗すると、書き切った一時ファイルが残る。放置すると
            // 保存が失敗し続ける環境で古い内容の .tmp が居座る
            try { File.Delete(tempPath); }
            catch (Exception cleanupEx)
            {
                DiagnosticLog.WriteFatal("settings", $"保存の一時ファイルを削除できなかった path={tempPath}: {cleanupEx}");
            }
            return false;
        }
    }
}
