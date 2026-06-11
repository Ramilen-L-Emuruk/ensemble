using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.UI.Settings;
using MultiTrackPlayer.UI.ViewModels;
using MultiTrackPlayer.UI.Windows;

namespace MultiTrackPlayer.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly KeyBindings _kb = new();
    private MixerWindow? _mixerWindow;
    private PlaylistWindow? _playlistWindow;
    private ChapterWindow? _chapterWindow;
    private WriteableBitmap? _bitmap;
    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _kb.Load();

        _vm.Engine.VideoFrameReady += Engine_VideoFrameReady;
        _vm.Engine.PositionChanged += (_, _) => UpdateSeekBarChapters();

        SeekBar.Seeking += (_, ratio) =>
            _vm.Engine.Seek(TimeSpan.FromSeconds(ratio * _vm.Duration.TotalSeconds));
    }

    private void Engine_VideoFrameReady(object? sender, VideoFrameData frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            VideoImage.Source = _bitmap;
        });
    }

    private void UpdateSeekBarChapters()
    {
        if (_vm.Duration <= TimeSpan.Zero) return;
        Dispatcher.BeginInvoke(() =>
        {
            var markers = _vm.Chapters.Select(c => (c.Chapter.StartTime.TotalSeconds / _vm.Duration.TotalSeconds, c.Title));
            SeekBar.SetChapters(markers);
        });
    }

    // ── Key handling ──
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        string keyStr = BuildKeyStr(e);
        string? cmd = _kb.GetCommand(keyStr);
        if (cmd == null) return;
        e.Handled = true;
        ExecuteCommand(cmd);
    }

    private static string BuildKeyStr(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        string prefix = string.Empty;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) prefix += "Ctrl+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) prefix += "Shift+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) prefix += "Alt+";
        return prefix + key.ToString();
    }

    private void ExecuteCommand(string cmd)
    {
        switch (cmd)
        {
            case "PlayPause":     _vm.PlayPauseCommand.Execute(null); break;
            case "Stop":          _vm.StopCommand.Execute(null); break;
            case "StepForward":   if (_vm.PlaybackState == PlaybackState.Paused) _vm.Engine.StepForward(); break;
            case "StepBackward":  if (_vm.PlaybackState == PlaybackState.Paused) _vm.Engine.StepBackward(); break;
            case "Skip+10":       _vm.Skip(10); break;
            case "Skip-10":       _vm.Skip(-10); break;
            case "Skip+3":        _vm.Skip(3); break;
            case "Skip-3":        _vm.Skip(-3); break;
            case "Skip+60":       _vm.Skip(60); break;
            case "Skip-60":       _vm.Skip(-60); break;
            case "VolumeUp":      _vm.MasterVolume = Math.Min(100, _vm.MasterVolume + 5); break;
            case "VolumeDown":    _vm.MasterVolume = Math.Max(0, _vm.MasterVolume - 5); break;
            case "SpeedUp":       _vm.ChangeSpeed(0.25); break;
            case "SpeedDown":     _vm.ChangeSpeed(-0.25); break;
            case "NextChapter":   _vm.Engine.JumpToNextChapter(); break;
            case "PrevChapter":   _vm.Engine.JumpToPreviousChapter(); break;
            case "NextFile":      _vm.PlayNext(); break;
            case "PrevFile":      _vm.PlayPrevious(); break;
            case "ToggleChapter": _vm.ToggleChapterAtCurrentPosition(); UpdateSeekBarChapters(); break;
            case "Fullscreen":    ToggleFullscreen(); break;
            case "Open":          OpenFileDialog(); break;
        }
    }

    private void OpenFileDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "動画ファイル|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.webm;*.flv|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.Playlist.AddFiles(new[] { dlg.FileName });
            _vm.OpenFile(dlg.FileName);
            UpdateSeekBarChapters();
        }
    }

    private void ToggleFullscreen()
    {
        if (WindowStyle != WindowStyle.None)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _prevWindowStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : _prevWindowStyle;
            WindowState = _prevWindowState;
        }
    }

    // ── Menu handlers ──
    private void MenuOpen_Click(object s, RoutedEventArgs e) => OpenFileDialog();
    private void MenuExit_Click(object s, RoutedEventArgs e) => Close();
    private void MenuMixer_Click(object s, RoutedEventArgs e) => GetMixer().Show();
    private void MenuPlaylist_Click(object s, RoutedEventArgs e) => GetPlaylist().Show();
    private void MenuChapter_Click(object s, RoutedEventArgs e) => GetChapter().Show();
    private void MenuFullscreen_Click(object s, RoutedEventArgs e) => ToggleFullscreen();
    private void MenuPlayPause_Click(object s, RoutedEventArgs e) => _vm.PlayPauseCommand.Execute(null);
    private void MenuStop_Click(object s, RoutedEventArgs e) => _vm.StopCommand.Execute(null);
    private void MenuStepFwd_Click(object s, RoutedEventArgs e) => _vm.Engine.StepForward();
    private void MenuStepBwd_Click(object s, RoutedEventArgs e) => _vm.Engine.StepBackward();

    // ── Transport ──
    private void PlayPause_Click(object s, RoutedEventArgs e) => _vm.PlayPauseCommand.Execute(null);
    private void SkipBack_Click(object s, RoutedEventArgs e) => _vm.Skip(-10);
    private void SkipFwd_Click(object s, RoutedEventArgs e) => _vm.Skip(10);
    private void PrevFile_Click(object s, RoutedEventArgs e) => _vm.PlayPrevious();
    private void NextFile_Click(object s, RoutedEventArgs e) => _vm.PlayNext();

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string s && double.TryParse(s, out double v))
            _vm.SetSpeed(v);
    }

    private void SpeedBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string s && double.TryParse(s, out double v))
            _vm.SetSpeed(v);
    }

    // ── Drag & Drop ──
    private void Window_DragOver(object sender, DragEventArgs e)
        => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _vm.Playlist.AddFiles(files);
            _vm.OpenFile(files[0]);
            UpdateSeekBarChapters();
        }
    }

    // ── Sub-windows (lazy) ──
    private MixerWindow GetMixer()
        => _mixerWindow ??= new MixerWindow(_vm) { Owner = this };
    private PlaylistWindow GetPlaylist()
        => _playlistWindow ??= new PlaylistWindow(_vm) { Owner = this };
    private ChapterWindow GetChapter()
        => _chapterWindow ??= new ChapterWindow(_vm) { Owner = this };

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}