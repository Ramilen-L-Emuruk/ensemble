using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.UI.Rendering;
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
    private D3DImagePresenter? _presenter;
    private AirspaceOverlayWindow? _overlay;
    private TimeSpan _lastRenderedPts = TimeSpan.MinValue;
    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle;
    // OnRendering 1回の処理時間調査用（ドロップ調査ログ専用）。1フレーム予算(60fpsで約16.7ms)の
    // 半分を超えたときだけ記録し、TryGetFrame と WritePixels のどちらが重いか切り分ける
    private const double RenderCostLogThresholdMs = 8.0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _kb.Load();

        // GPU が利用可能なら D3DImage による GPU ゼロコピー描画経路を使う。
        // RenderCapability.Tier の上位 16bit が 0 の場合はソフトウェアレンダリングのため従来の
        // WriteableBitmap 経路にフォールバックする。初期化に失敗した場合も同様にフォールバックする。
        if (RenderCapability.Tier >> 16 > 0)
        {
            try
            {
                _presenter = new D3DImagePresenter();
                VideoImage.Source = _presenter.D3DImage;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("d3dPresenter", $"初期化失敗のため WriteableBitmap にフォールバック: {ex.Message}");
                _presenter = null;
            }
        }

        // SpeedBox はプリセット項目の静的な ComboBox で PlaybackSpeed に双方向バインドしていないため、
        // キーボードショートカットやメニューからの速度変更を選択表示へ手動で反映する
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed)) SyncSpeedBox();
            // ファイル切替で映像サイズ（アスペクト比）が変わるため、映像面のレターボックス矩形を再計算する（段階2-1）
            else if (e.PropertyName == nameof(MainViewModel.CurrentMedia)) UpdateVideoHostLayout();
        };

        SeekBar.Seeking += (_, ratio) =>
            _vm.Engine.Seek(TimeSpan.FromSeconds(ratio * _vm.Duration.TotalSeconds));
        // フルスクリーン時のシークバー・無操作タイマー・マウス移動検知は AirspaceOverlayWindow 側へ移設した（段階2-2b）。

        CompositionTarget.Rendering += OnRendering;

        // コマンドライン引数で渡された動画ファイルを起動時に開く。
        // 1件だけ渡された場合はフォルダ内の他の動画も自動でプレイリストに読み込む
        Loaded += (_, _) =>
        {
            // 案Y: 映像子ウィンドウの HWND をエンジンへ接続する。GPU デコード経路の再生開始時に
            // この HWND へスワップチェーンが張られ、vout スレッドが vsync で Present する。
            _vm.Engine.AttachVideoOutput(VideoHost.Hwnd);

            // 案Y: 映像子ウィンドウが airspace で最前面になり OSD が隠れるため、透過オーバーレイを
            // その上に重ねて追従表示する（段階2-2a）。VideoHost のサイズ・ウィンドウ位置・状態変化で再配置する。
            _overlay = new AirspaceOverlayWindow { Owner = this, DataContext = _vm };
            // フルスクリーン中はオーバーレイが前面に来るため、万一オーバーレイ側にキーが渡っても
            // 本ウィンドウのショートカット処理へ転送してキー操作を失わないようにする
            _overlay.KeyDown += Window_KeyDown;
            VideoHost.SizeChanged += (_, _) => UpdateOverlayBounds();
            LocationChanged += (_, _) => UpdateOverlayBounds();
            StateChanged += (_, _) => UpdateOverlayBounds();
            UpdateOverlayBounds();

            var files = App.StartupArgs.Where(System.IO.File.Exists).ToArray();
            if (files.Length == 0) return;
            if (files.Length == 1)
            {
                _vm.OpenFileWithFolderPlaylist(files[0]);
            }
            else
            {
                _vm.Playlist.AddFiles(files);
                _vm.OpenFile(files[0]);
            }
            UpdateSeekBarChapters();
        };
    }

    // 映像フレームをエンジンからプルする。VideoFrameLease は byte[] を経由せずネイティブバッファを
    // 直接扱うため、毎フレームの確保が発生しない。描画先は GPU 経路（D3DImagePresenter）優先で、
    // GPU 非対応・初期化失敗時のみ WriteableBitmap にフォールバックする。
    private void OnRendering(object? sender, EventArgs e)
    {
        // vout（スワップチェーン）稼働中は映像提示を専用スレッドが担うため、UI プルは行わない（案Y）。
        if (_vm.Engine.IsVideoOutputActive) return;

        long t0 = Stopwatch.GetTimestamp();
        var lease = _vm.Engine.TryGetFrame(_vm.Engine.Position);
        long t1 = Stopwatch.GetTimestamp();
        if (lease == null) return;

        if (lease.Pts == _lastRenderedPts)
        {
            _vm.Engine.ReturnFrame(lease);
            return;
        }

        if (_presenter != null)
        {
            // D3DImage 経路: Present が Kind に応じて GPU ゼロコピー / CPU アップロードへ振り分ける。
            _presenter.Present(lease);
        }
        else if (lease.Kind == FrameKind.Cpu)
        {
            // フォールバック経路: WriteableBitmap へ CPU コピーする。
            if (_bitmap is null || _bitmap.PixelWidth != lease.Width || _bitmap.PixelHeight != lease.Height)
                _bitmap = new WriteableBitmap(lease.Width, lease.Height, 96, 96, PixelFormats.Bgra32, null);

            _bitmap.WritePixels(
                new Int32Rect(0, 0, lease.Width, lease.Height),
                lease.PixelBuffer, lease.Stride * lease.Height, lease.Stride);
            VideoImage.Source = _bitmap;
        }
        // else: presenter 無し（WPF ソフトウェアレンダリング）かつ GPU リースは D3D を持たず表示できないため、
        // この稀な組み合わせでは当該フレームをスキップする（クラッシュ回避）。
        long t2 = Stopwatch.GetTimestamp();
        _lastRenderedPts = lease.Pts;

        _vm.Engine.ReturnFrame(lease);

        double tryGetFrameMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        // t1〜t2 の計測対象は、GPU 経路では Present 全体、フォールバックでは WritePixels。
        // ラベルは両経路を包含する意味で uploadMs とする（旧名 writePixels から意味変更）。
        double uploadMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
        if (tryGetFrameMs + uploadMs > RenderCostLogThresholdMs)
        {
            DiagnosticLog.Write("renderCost",
                $"total={tryGetFrameMs + uploadMs:F1}ms tryGetFrame={tryGetFrameMs:F1}ms uploadMs={uploadMs:F1}ms w={lease.Width} h={lease.Height}");
        }
    }

    private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVideoHostLayout();

    // 映像面（VideoHost 子ウィンドウ）を映像アスペクト比のレターボックス矩形に合わせて中央配置する（段階2-1）。
    // swapchain は映像サイズ + Scaling.Stretch のままで、子ウィンドウのアスペクトを映像に一致させることで歪みを防ぐ。
    // 周囲は親 Grid（VideoArea）の黒背景がそのまま黒帯になる。
    private void UpdateVideoHostLayout()
    {
        var media = _vm.CurrentMedia;
        if (media == null || media.Width <= 0 || media.Height <= 0)
        {
            // 未オープン（ドロップ待ち）時は映像面を出さない
            VideoHost.Width = 0;
            VideoHost.Height = 0;
            return;
        }

        double areaW = VideoArea.ActualWidth;
        double areaH = VideoArea.ActualHeight;
        if (areaW <= 0 || areaH <= 0) return;

        double videoAspect = (double)media.Width / media.Height;
        double areaAspect = areaW / areaH;

        double hostW, hostH;
        if (areaAspect > videoAspect)
        {
            // 領域が横長 → 高さ基準（左右に黒帯）
            hostH = areaH;
            hostW = areaH * videoAspect;
        }
        else
        {
            // 領域が縦長 → 幅基準（上下に黒帯）
            hostW = areaW;
            hostH = areaW / videoAspect;
        }

        VideoHost.Width = hostW;
        VideoHost.Height = hostH;
    }

    // 透過オーバーレイ（AirspaceOverlayWindow）を映像子ウィンドウ（VideoHost）のスクリーン座標・サイズへ
    // 追従させる（段階2-2a）。映像未オープンで VideoHost が 0×0 のときはオーバーレイを隠す。
    private void UpdateOverlayBounds()
    {
        if (_overlay == null) return;
        var source = PresentationSource.FromVisual(VideoHost);
        if (source == null) return;

        double width = VideoHost.ActualWidth;
        double height = VideoHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        // PointToScreen はデバイスピクセル、Window.Left/Top はデバイス非依存単位のため DPI 変換する
        Point deviceTopLeft = VideoHost.PointToScreen(new Point(0, 0));
        Point dipTopLeft = source.CompositionTarget.TransformFromDevice.Transform(deviceTopLeft);
        _overlay.Left = dipTopLeft.X;
        _overlay.Top = dipTopLeft.Y;
        _overlay.Width = width;
        _overlay.Height = height;
        if (!_overlay.IsVisible) _overlay.Show();
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
        if (_vm.IsFullscreen) _overlay?.PokeFullscreenBar();

        HandleShortcutKey(e);
    }

    /// <summary>キーバインドに従いショートカットコマンドを実行する。サブウィンドウ表示中も
    /// メインウィンドウのショートカットが使えるよう、各サブウィンドウのキー入力からも呼び出される。</summary>
    public void HandleShortcutKey(KeyEventArgs e)
    {
        if (e.IsRepeat) { e.Handled = true; return; }
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
            case Key.S:
                track.IsSolo = !track.IsSolo;
                _vm.ShowOsd($"{track.Name} {(track.IsSolo ? "ソロ" : "ソロ解除")}");
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
        var dlg = new OpenFileDialog { Filter = SupportedVideoExtensions.OpenFileDialogFilter };
        if (dlg.ShowDialog() == true)
        {
            _vm.OpenFileWithFolderPlaylist(dlg.FileName);
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
            _overlay?.EnterFullscreen(); // 切替直後は一旦見せて、無操作なら自動的に消える
            // オーバーレイが前面に来てもキーボード入力は本ウィンドウで受けるよう、アクティブ状態を取り戻す
            Activate();
            Focus();
            _vm.ShowOsd("フルスクリーン");
        }
        else
        {
            WindowStyle = _prevWindowStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : _prevWindowStyle;
            WindowState = _prevWindowState;
            AppMenu.Visibility = Visibility.Visible;
            TransportBar.Visibility = Visibility.Visible;
            _vm.IsFullscreen = false;
            _overlay?.ExitFullscreen();
            _vm.ShowOsd("フルスクリーン解除");
        }
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
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        if (files.Length == 1)
        {
            _vm.OpenFileWithFolderPlaylist(files[0]);
        }
        else
        {
            _vm.Playlist.AddFiles(files);
            _vm.OpenFile(files[0]);
        }
        UpdateSeekBarChapters();
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
        _overlay?.Close();
        _presenter?.Dispose();
        _vm.Dispose();
        base.OnClosed(e);
    }
}