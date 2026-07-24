namespace MultiTrackPlayer.UI;

/// <summary>アプリが動画ファイルとして扱う拡張子。ファイル選択ダイアログのフィルターと
/// フォルダ内自動プレイリスト化の両方から参照される単一の情報源。</summary>
internal static class SupportedVideoExtensions
{
    public static readonly string[] Extensions =
        { ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".flv" };

    public const string OpenFileDialogFilter =
        "動画ファイル|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.webm;*.flv|すべてのファイル|*.*";
}
