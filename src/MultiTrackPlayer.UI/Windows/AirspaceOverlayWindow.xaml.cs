using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

/// <summary>
/// 映像子ウィンドウ（案Y の <c>VideoHwndHost</c>）が airspace で最前面になるため、その上に重ねる
/// 透過レイヤードウィンドウ。OSD とフルスクリーン時の下部オーバーレイ（シークバー）をここに載せ、
/// <c>MainWindow</c> から映像子ウィンドウのスクリーン座標へ追従させる。
/// 通常はウィンドウ全体を <c>WS_EX_TRANSPARENT</c> でクリックスルーにして下の映像・入力を妨げず、
/// フルスクリーン中のみ <see cref="SetClickThrough"/> で入力を受け付ける（段階2-2a: OSD / 段階2-2b: フルスクリーン）。
/// </summary>
public partial class AirspaceOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // フルスクリーン中に下部オーバーレイ（シークバー）を無操作で自動的に隠すタイマー
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _isFullscreen;

    public AirspaceOverlayWindow()
    {
        InitializeComponent();

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FullscreenOverlay.Visibility = Visibility.Collapsed;
            Cursor = Cursors.None;
        };

        // フルスクリーン中はマウス移動で下部オーバーレイを一時表示する（無操作で自動的に消える）
        MouseMove += (_, _) => { if (_isFullscreen) ShowFullscreenBar(); };

        // 下部シークバーのドラッグでシークする（DataContext は MainWindow が設定する MainViewModel）
        FullscreenSeekBar.Seeking += (_, ratio) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Engine.Seek(TimeSpan.FromSeconds(ratio * vm.Duration.TotalSeconds));
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 常時付ける拡張スタイル: レイヤード + Alt+Tab 除外 + 非アクティブ化。
        // クリックスルー（WS_EX_TRANSPARENT）は SetClickThrough で切り替える（初期は ON）。
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetClickThrough(true);
    }

    /// <summary>フルスクリーン開始。クリックスルーを解除して入力を受け付け、下部オーバーレイを一旦表示する。</summary>
    public void EnterFullscreen()
    {
        _isFullscreen = true;
        SetClickThrough(false);
        ShowFullscreenBar();
    }

    /// <summary>フルスクリーン終了。下部オーバーレイを隠し、クリックスルーへ戻す。</summary>
    public void ExitFullscreen()
    {
        _isFullscreen = false;
        _hideTimer.Stop();
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        SetClickThrough(true);
    }

    /// <summary>フルスクリーン中にキー操作等で下部オーバーレイを再表示する。フルスクリーン外では何もしない。</summary>
    public void PokeFullscreenBar()
    {
        if (_isFullscreen) ShowFullscreenBar();
    }

    private void ShowFullscreenBar()
    {
        FullscreenOverlay.Visibility = Visibility.Visible;
        Cursor = Cursors.Arrow;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // クリックスルーの切替。WPF のヒットテストと Win32 の WS_EX_TRANSPARENT を揃える。さらに全面で
    // マウス移動を拾えるよう、RootGrid の背景（透明でもヒットテスト対象になる Transparent ブラシ）を付け外しする。
    private void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }
        IsHitTestVisible = !clickThrough;
        RootGrid.Background = clickThrough ? null : Brushes.Transparent;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
