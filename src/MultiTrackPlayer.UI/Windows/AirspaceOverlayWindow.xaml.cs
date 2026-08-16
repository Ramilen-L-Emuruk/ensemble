using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

/// <summary>
/// 映像子ウィンドウ（案Y の <c>VideoHwndHost</c>）が airspace で最前面になるため、その上に重ねる
/// 透過レイヤードウィンドウ。OSD とフルスクリーン時の下部オーバーレイ（シークバー）をここに載せ、
/// <c>MainWindow</c> から映像領域（<c>VideoArea</c>）のスクリーン座標へ追従させる。
/// 通常はウィンドウ全体を <c>WS_EX_TRANSPARENT</c> でクリックスルーにして下の映像・入力を妨げず、
/// フルスクリーン中のみ <see cref="SetClickThrough"/> で入力を受け付ける（段階2-2a: OSD / 段階2-2b: フルスクリーン）。
/// ただし入力を受け付ける状態でも、実際に当たるのは不透明に描画されている部分（＝下部オーバーレイ）だけで、
/// 透明な領域はクリックスルーのまま。このウィンドウはアクティブ化しない（<c>WS_EX_NOACTIVATE</c>）ので、
/// 全面を入力対象にするとアプリを前面に戻すクリックまで吸ってしまうため、この線引きは崩さないこと。
/// </summary>
public partial class AirspaceOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // フルスクリーン中のマウス移動を監視する間隔。オーバーレイの透明部分はクリックスルーで
    // マウスメッセージが下のウィンドウへ抜けるため、WPF の MouseMove では映像上の移動を拾えない
    // （下部オーバーレイが消えている間はウィンドウ全面が透明になり、一切通知が来なくなる）。
    // カーソル座標を定期的に見て動きを検知する。
    private static readonly TimeSpan CursorWatchInterval = TimeSpan.FromMilliseconds(150);

    // フルスクリーン中に下部オーバーレイ（シークバー）を無操作で自動的に隠すタイマー
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _cursorWatchTimer = new() { Interval = CursorWatchInterval };
    private Point _lastCursorPos;
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
        _cursorWatchTimer.Tick += (_, _) =>
        {
            if (!TryGetCursorPos(out Point pos) || pos == _lastCursorPos) return;
            _lastCursorPos = pos;
            ShowFullscreenBar();
        };

        // 下部シークバーのドラッグでシークする（DataContext は MainWindow が設定する MainViewModel）
        FullscreenSeekBar.Seeking += (_, ratio) =>
        {
            if (DataContext is MainViewModel vm)
                vm.SeekTo(TimeSpan.FromSeconds(ratio * vm.Duration.TotalSeconds));
        };

        PreviewMouseUp += (_, _) => RestoreOwnerFocus();
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
        // 開始時点の座標を基準にして、カーソルが動いていないのに表示が復活するのを防ぐ
        TryGetCursorPos(out _lastCursorPos);
        _cursorWatchTimer.Start();
        ShowFullscreenBar();
    }

    /// <summary>フルスクリーン終了。下部オーバーレイを隠し、クリックスルーへ戻す。</summary>
    public void ExitFullscreen()
    {
        _isFullscreen = false;
        _cursorWatchTimer.Stop();
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

    // このウィンドウはキーボードフォーカスを持たない前提で作ってある（Focusable="False" /
    // ShowActivated="False" / WS_EX_NOACTIVATE）。それでもフルスクリーン中にシークバーをクリックすると
    // Win32 のフォーカスがこちらへ移り、WPF 側にフォーカスを受けられる要素が無いため
    // キー入力の行き先が消えてしまう（＝以降ショートカットが一切効かなくなる）ので、オーナー
    // （MainWindow）へ返して復帰させる。
    // 返すのはボタンを離した後。WM_SETFOCUS を握ってその場で奪い返すと、クリック処理の最中に
    // フォーカスが動いてしまい WPF がこのウィンドウのマウス入力を配送しなくなる（シークバーが
    // クリックに反応しなくなる）ため、その方式は採らない。
    private void RestoreOwnerFocus()
    {
        if (Owner == null) return;

        IntPtr ownerHwnd = new WindowInteropHelper(Owner).Handle;
        if (ownerHwnd != IntPtr.Zero) SetFocus(ownerHwnd);
    }

    // クリックスルーの切替。WPF のヒットテストと Win32 の WS_EX_TRANSPARENT を揃える。
    // 解除しても当たるのは不透明に描画されている部分だけで、透明な領域は素通しのまま
    // （レイヤードウィンドウのヒットテストは描画結果のアルファ値で決まるため）。
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
    }

    private static bool TryGetCursorPos(out Point pos)
    {
        if (GetCursorPos(out POINT p))
        {
            pos = new Point(p.X, p.Y);
            return true;
        }
        pos = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
