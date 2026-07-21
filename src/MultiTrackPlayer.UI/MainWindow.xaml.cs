using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Engine.Diagnostics;
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
    private ShortcutsWindow? _shortcutsWindow;
    private DebugWindow? _debugWindow;
    private WriteableBitmap? _bitmap;
    private TimeSpan _lastRenderedPts = TimeSpan.MinValue;
    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle;
    private readonly DispatcherTimer _overlayHideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    // OnRendering 1回の処理時間調査用（ドロップ調査ログ専用）。1フレーム予算(60fpsで約16.7ms)の
    // 半分を超えたときだけ記録し、TryGetFrame と WritePixels のどちらが重いか切り分ける
    private const double RenderCostLogThresholdMs = 8.0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _kb.Load();

        // SpeedBox はプリセット項目の静的な ComboBox で PlaybackSpeed に双方向バインドしていないため、
        // キーボードショートカットやメニューからの速度変更を選択表示へ手動で反映する
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed)) SyncSpeedBox();
        };

        SeekBar.Seeking += (_, ratio) =>
            _vm.Engine.Seek(TimeSpan.FromSeconds(ratio * _vm.Duration.TotalSeconds));
        FullscreenSeekBar.Seeking += (_, ratio) =>
            _vm.Engine.Seek(TimeSpan.FromSeconds(ratio * _vm.Duration.TotalSeconds));

        _overlayHideTimer.Tick += (_, _) =>
        {
            _overlayHideTimer.Stop();
            FullscreenOverlay.Visibility = Visibility.Collapsed;
        };
        MouseMove += (_, _) => { if (_vm.IsFullscreen) ShowFullscreenOverlay(); };

        CompositionTarget.Rendering += OnRendering;

        // コマンドライン引数で渡された動画ファイルを起動時に開く
        Loaded += (_, _) =>
        {
            var files = App.StartupArgs.Where(System.IO.File.Exists).ToArray();
            if (files.Length == 0) return;
            _vm.Playlist.AddFiles(files);
            _vm.OpenFile(files[0]);
            UpdateSeekBarChapters();
        };
    }

    // 映像フレームをエンジンからプルする。VideoFrameLease は byte[] を経由せず
    // ネイティブバッファから直接 WritePixels するため、毎フレームの確保が発生しない。
    private void OnRendering(object? sender, EventArgs e)
    {
        long t0 = Stopwatch.GetTimestamp();
        var lease = _vm.Engine.TryGetFrame(_vm.Engine.Position);
        long t1 = Stopwatch.GetTimestamp();
        if (lease == null) return;

        if (lease.Pts == _lastRenderedPts)
        {
            _vm.Engine.ReturnFrame(lease);
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != lease.Width || _bitmap.PixelHeight != lease.Height)
            _bitmap = new WriteableBitmap(lease.Width, lease.Height, 96, 96, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(
            new Int32Rect(0, 0, lease.Width, lease.Height),
            lease.PixelBuffer, lease.Stride * lease.Height, lease.Stride);
        VideoImage.Source = _bitmap;
        long t2 = Stopwatch.GetTimestamp();
        _lastRenderedPts = lease.Pts;

        _vm.Engine.ReturnFrame(lease);

        double tryGetFrameMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        double writePixelsMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
        if (tryGetFrameMs + writePixelsMs > RenderCostLogThresholdMs)
        {
            DiagnosticLog.Write("renderCost",
                $"total={tryGetFrameMs + writePixelsMs:F1}ms tryGetFrame={tryGetFrameMs:F1}ms writePixels={writePixelsMs:F1}ms w={lease.Width} h={lease.Height}");
        }
    }

    private void SyncSpeedBox()
    {
        foreach (System.Windows.Controls.ComboBoxItem item in SpeedBox.Items)
        {
            if (item.Tag is string s && double.TryParse(s, out double v) &&
                Math.Abs(v - _vm.PlaybackSpeed) < 0.001)
            {
                SpeedBox.SelectedItem = item;
                return;
            }
        }
        SpeedBox.SelectedIndex = -1;
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
        // OS のキーリピートを無視する。矢印キーを押しっぱなしにして巻き戻すと、
        // 前のシークの映像プリロールが終わる前に次の Skip が発行され続け、
        // シークパイプラインが常に再武装された状態になって映像が乱れる原因になる
        if (e.IsRepeat) { e.Handled = true; return; }

        if (e.Key == Key.Escape && _vm.IsFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (_vm.IsFullscreen) ShowFullscreenOverlay();

        if (TryHandleTrackKey(e)) { e.Handled = true; return; }

        string keyStr = BuildKeyStr(e);
        string? cmd = _kb.GetCommand(keyStr);
        if (cmd == null) return;
        e.Handled = true;
        ExecuteCommand(cmd);
    }

    // 数字キー(1〜9、テンキー含む)を押しっぱなしにしながら M/↑/↓ を押すと、
    // その番号のトラックに対してミュート切替・音量±5%を行う。
    // 対象トラックが存在しない場合は通常のキーバインド処理にフォールスルーする。
    private bool TryHandleTrackKey(KeyEventArgs e)
    {
        if (!TryGetHeldTrackNumber(out int trackNumber)) return false;

        AudioTrackViewModel? track = null;
        foreach (var t in _vm.AudioTracks)
        {
            if (t.TrackNumber == trackNumber) { track = t; break; }
        }
        if (track == null) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.M:
                track.IsMuted = !track.IsMuted;
                _vm.ShowOsd($"{track.Name} {(track.IsMuted ? "ミュート" : "ミュート解除")}");
                return true;
            case Key.Up:
                track.Volume = Math.Min(200, track.Volume + 5);
                _vm.ShowOsd($"{track.Name} 音量 {track.Volume:0}%");
                return true;
            case Key.Down:
                track.Volume = Math.Max(0, track.Volume - 5);
                _vm.ShowOsd($"{track.Name} 音量 {track.Volume:0}%");
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetHeldTrackNumber(out int trackNumber)
    {
        for (int i = 1; i <= 9; i++)
        {
            var digitKey = (Key)((int)Key.D0 + i);
            var numpadKey = (Key)((int)Key.NumPad0 + i);
            if (Keyboard.IsKeyDown(digitKey) || Keyboard.IsKeyDown(numpadKey))
            {
                trackNumber = i;
                return true;
            }
        }
        trackNumber = 0;
        return false;
    }

    // WPF の Key 列挙体は一部の値が別名（エイリアス）を持ち、ToString() が
    // キーバインド辞書とは異なる別名を返すことがある（例: PageDown と Next は同じ値で
    // ToString() は "Next" を返す）。辞書に登録している名前に正規化する。
    private static readonly Dictionary<Key, string> KeyAliasNormalization = new()
    {
        [Key.Next] = "PageDown",
        [Key.Prior] = "PageUp",
        [Key.Oem6] = "OemCloseBrackets",
        [Key.Oem4] = "OemOpenBrackets",
    };

    private static string BuildKeyStr(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        string prefix = string.Empty;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) prefix += "Ctrl+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) prefix += "Shift+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) prefix += "Alt+";
        string keyName = KeyAliasNormalization.TryGetValue(key, out var normalized) ? normalized : key.ToString();
        return prefix + keyName;
    }

    private void ExecuteCommand(string cmd)
    {
        switch (cmd)
        {
            case "PlayPause":     _vm.PlayPauseCommand.Execute(null); break;
            case "Stop":          _vm.StopCommand.Execute(null); break;
            case "StepForward":   _vm.StepForwardCommand.Execute(null); break;
            case "StepBackward":  _vm.StepBackwardCommand.Execute(null); break;
            case "Skip+10":       _vm.Skip(10); break;
            case "Skip-10":       _vm.Skip(-10); break;
            case "Skip+3":        _vm.Skip(3); break;
            case "Skip-3":        _vm.Skip(-3); break;
            case "Skip+60":       _vm.Skip(60); break;
            case "Skip-60":       _vm.Skip(-60); break;
            case "VolumeUp":      _vm.MasterVolume = Math.Min(100, _vm.MasterVolume + 5); break;
            case "VolumeDown":    _vm.MasterVolume = Math.Max(0, _vm.MasterVolume - 5); break;
            case "Mute":          _vm.ToggleMuteCommand.Execute(null); break;
            case "SpeedUp":       _vm.ChangeSpeed(0.25); break;
            case "SpeedDown":     _vm.ChangeSpeed(-0.25); break;
            case "NextChapter":   _vm.Engine.JumpToNextChapter(); _vm.ShowOsd("次のチャプター"); break;
            case "PrevChapter":   _vm.Engine.JumpToPreviousChapter(); _vm.ShowOsd("前のチャプター"); break;
            case "NextFile":      _vm.PlayNext(); break;
            case "PrevFile":      _vm.PlayPrevious(); break;
            case "ToggleChapter": _vm.ToggleChapterAtCurrentPosition(); UpdateSeekBarChapters(); break;
            case "Fullscreen":    ToggleFullscreen(); break;
            case "Open":          OpenFileDialog(); break;
            case "ShowShortcuts": GetShortcuts().Show(); break;
            case "ShowMixer":     GetMixer().Show(); break;
            case "ShowPlaylist":  GetPlaylist().Show(); break;
            case "ShowChapter":   GetChapter().Show(); break;
            case "ShowDebug":     GetDebug().Show(); break;
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
            // メニュー・トランスポートバーを消し、映像用 Grid が DockPanel の残り全域（＝画面全体）を占めるようにする
            AppMenu.Visibility = Visibility.Collapsed;
            TransportBar.Visibility = Visibility.Collapsed;
            _vm.IsFullscreen = true;
            ShowFullscreenOverlay(); // 切替直後は一旦見せて、無操作なら自動的に消える
            _vm.ShowOsd("フルスクリーン");
        }
        else
        {
            WindowStyle = _prevWindowStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : _prevWindowStyle;
            WindowState = _prevWindowState;
            AppMenu.Visibility = Visibility.Visible;
            TransportBar.Visibility = Visibility.Visible;
            _vm.IsFullscreen = false;
            _overlayHideTimer.Stop();
            FullscreenOverlay.Visibility = Visibility.Collapsed;
            _vm.ShowOsd("フルスクリーン解除");
        }
    }

    /// <summary>フルスクリーン中にシークバー＋現在時刻/長さのオーバーレイを表示し、無操作タイマーをリセットする。</summary>
    private void ShowFullscreenOverlay()
    {
        FullscreenOverlay.Visibility = Visibility.Visible;
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    // ── Menu handlers ──
    private void MenuOpen_Click(object s, RoutedEventArgs e) => OpenFileDialog();
    private void MenuExit_Click(object s, RoutedEventArgs e) => Close();
    private void MenuMixer_Click(object s, RoutedEventArgs e) => GetMixer().Show();
    private void MenuPlaylist_Click(object s, RoutedEventArgs e) => GetPlaylist().Show();
    private void MenuChapter_Click(object s, RoutedEventArgs e) => GetChapter().Show();
    private void MenuDebug_Click(object s, RoutedEventArgs e) => GetDebug().Show();
    private void MenuFullscreen_Click(object s, RoutedEventArgs e) => ToggleFullscreen();
    private void MenuShortcuts_Click(object s, RoutedEventArgs e) => GetShortcuts().Show();
    private void MenuPlayPause_Click(object s, RoutedEventArgs e) => _vm.PlayPauseCommand.Execute(null);
    private void MenuStop_Click(object s, RoutedEventArgs e) => _vm.StopCommand.Execute(null);
    private void MenuStepFwd_Click(object s, RoutedEventArgs e) => _vm.StepForwardCommand.Execute(null);
    private void MenuStepBwd_Click(object s, RoutedEventArgs e) => _vm.StepBackwardCommand.Execute(null);
    private void MenuSaveDefaultMutes_Click(object s, RoutedEventArgs e) => _vm.SaveCurrentMutesAsDefault();

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
    private DebugWindow GetDebug()
        => _debugWindow ??= new DebugWindow(_vm) { Owner = this };
    private ShortcutsWindow GetShortcuts()
        => _shortcutsWindow ??= new ShortcutsWindow(_kb) { Owner = this };

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _vm.Dispose();
        base.OnClosed(e);
    }
}