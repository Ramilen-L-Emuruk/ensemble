using System.Windows;
using MultiTrackPlayer.UI.Settings;

namespace MultiTrackPlayer.UI.Windows;

public partial class ShortcutsWindow : Window
{
    private static readonly Dictionary<string, string> CommandDisplayNames = new()
    {
        ["PlayPause"]     = "再生 / 一時停止",
        ["Stop"]          = "停止",
        ["StepForward"]   = "コマ送り",
        ["StepBackward"]  = "コマ戻し",
        ["Skip+10"]       = "10秒 早送り",
        ["Skip-10"]       = "10秒 巻き戻し",
        ["Skip+3"]        = "3秒 早送り",
        ["Skip-3"]        = "3秒 巻き戻し",
        ["Skip+60"]       = "60秒 早送り",
        ["Skip-60"]       = "60秒 巻き戻し",
        ["VolumeUp"]      = "音量を上げる",
        ["VolumeDown"]    = "音量を下げる",
        ["Mute"]          = "ミュート切替",
        ["Fullscreen"]    = "フルスクリーン切替",
        ["Open"]          = "ファイルを開く",
        ["SpeedUp"]       = "再生速度を上げる",
        ["SpeedDown"]     = "再生速度を下げる",
        ["NextChapter"]   = "次のチャプターへ",
        ["PrevChapter"]   = "前のチャプターへ",
        ["NextFile"]      = "次のファイルを再生",
        ["PrevFile"]      = "前のファイルを再生",
        ["ToggleChapter"] = "現在位置にチャプター追加/削除",
        ["ShowShortcuts"] = "ショートカット一覧を表示",
    };

    private static readonly Dictionary<string, string> KeyDisplayNames = new()
    {
        ["OemPeriod"]        = ".",
        ["OemComma"]         = ",",
        ["OemOpenBrackets"]  = "[",
        ["OemCloseBrackets"] = "]",
        ["Left"]             = "←",
        ["Right"]            = "→",
        ["Up"]               = "↑",
        ["Down"]             = "↓",
    };

    public ShortcutsWindow(KeyBindings kb)
    {
        InitializeComponent();
        ShortcutList.ItemsSource = kb.Bindings
            .Select(b => new ShortcutItem(FormatKey(b.Key), FormatCommand(b.Value)))
            .ToList();
    }

    private static string FormatKey(string keyStr)
    {
        var parts = keyStr.Split('+');
        parts[^1] = KeyDisplayNames.GetValueOrDefault(parts[^1], parts[^1]);
        return string.Join("+", parts);
    }

    private static string FormatCommand(string command) =>
        CommandDisplayNames.GetValueOrDefault(command, command);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private record ShortcutItem(string Key, string Action);
}
