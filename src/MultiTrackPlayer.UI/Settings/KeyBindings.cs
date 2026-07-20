using System.IO;
using System.Text.Json;

namespace MultiTrackPlayer.UI.Settings;

public class KeyBindings
{
    private static readonly string FilePath =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "MultiTrackPlayer", "keybindings.json");

    public Dictionary<string, string> Bindings { get; private set; } = DefaultBindings();

    private static Dictionary<string, string> DefaultBindings() => new()
    {
        ["Space"]        = "PlayPause",
        ["S"]            = "Stop",
        ["OemPeriod"]    = "StepForward",
        ["OemComma"]     = "StepBackward",
        ["Right"]        = "Skip+10",
        ["Left"]         = "Skip-10",
        ["Shift+Right"]  = "Skip+3",
        ["Shift+Left"]   = "Skip-3",
        ["Ctrl+Right"]   = "Skip+60",
        ["Ctrl+Left"]    = "Skip-60",
        ["Up"]           = "VolumeUp",
        ["Down"]         = "VolumeDown",
        ["M"]            = "Mute",
        ["F"]            = "Fullscreen",
        ["F11"]          = "Fullscreen",
        ["Ctrl+O"]       = "Open",
        ["Ctrl+M"]       = "ShowMixer",
        ["Ctrl+L"]       = "ShowPlaylist",
        ["Ctrl+T"]       = "ShowChapter",
        ["OemCloseBrackets"] = "SpeedUp",
        ["OemOpenBrackets"]  = "SpeedDown",
        ["PageDown"]     = "NextChapter",
        ["PageUp"]       = "PrevChapter",
        ["N"]            = "NextFile",
        ["P"]            = "PrevFile",
        ["C"]            = "ToggleChapter",
        ["F1"]           = "ShowShortcuts",
    };

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded != null) Bindings = loaded;
        }
        catch { }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Bindings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetCommand(string keyStr) =>
        Bindings.TryGetValue(keyStr, out var cmd) ? cmd : null;
}