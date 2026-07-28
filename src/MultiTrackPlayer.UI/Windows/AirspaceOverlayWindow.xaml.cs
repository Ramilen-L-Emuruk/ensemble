using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MultiTrackPlayer.UI.Windows;

/// <summary>
/// 映像子ウィンドウ（案Y の <c>VideoHwndHost</c>）が airspace で最前面になるため、その上に重ねる
/// 透過レイヤードウィンドウ。OSD などのオーバーレイをここに載せ、<c>MainWindow</c> から映像子ウィンドウの
/// スクリーン座標へ追従させる（段階2-2a では OSD のみ）。
/// ウィンドウ全体を <c>WS_EX_TRANSPARENT</c> でクリックスルーにし、下の映像表示・入力（ファイルドロップ等）を妨げない。
/// </summary>
public partial class AirspaceOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public AirspaceOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Win32 レベルでクリック・入力を透過させ、Alt+Tab とフォーカス（アクティブ化）からも外す。
        // WPF の IsHitTestVisible だけでは Win32 のヒットテストは透過しないため拡張スタイルで補う。
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
