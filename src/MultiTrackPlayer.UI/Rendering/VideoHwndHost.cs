using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MultiTrackPlayer.UI.Rendering;

/// <summary>
/// 映像専用の Win32 子ウィンドウを WPF に埋め込む <see cref="HwndHost"/>。この子 HWND に D3D11 スワップチェーンを
/// 直接 Present することで、D3DImage（DWM 合成）を経由せず提示レートを vsync に安定させる（案Y・段階1）。
///
/// airspace の制約により、この子ウィンドウ領域には WPF 要素を重ねられない（OSD・フルスクリーンオーバーレイは
/// 透過レイヤードウィンドウ <c>AirspaceOverlayWindow</c> で対応済み）。
///
/// スレッド契約: 生成・破棄は UI スレッドで行う。子ウィンドウのハンドル（<see cref="Hwnd"/>）に対する
/// スワップチェーン生成も UI スレッドで行い、Present だけを別スレッド（vout）から呼ぶ想定。
/// </summary>
public sealed class VideoHwndHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const uint WM_SIZE = 0x0005;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private const string ClassName = "MtpVideoHwndHostWindow";
    // WndProc デリゲートは GC に回収されると関数ポインタが無効になるため、プロセス寿命で静的保持する。
    private static WndProcDelegate? _wndProcHolder;

    private IntPtr _hwnd;

    /// <summary>子ウィンドウのハンドル。スワップチェーン生成に使う。生成前は <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>子ウィンドウがサイズ変更されたとき（引数は新しいクライアント領域のピクセル幅・高さ）。スワップチェーンの ResizeBuffers に使う。</summary>
    public event Action<int, int>? ClientSizeChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();
        _hwnd = CreateWindowEx(0, ClassName, string.Empty, WS_CHILD | WS_VISIBLE,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"映像子ウィンドウの生成に失敗しました（Win32 error={Marshal.GetLastWin32Error()}）");
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == WM_SIZE)
        {
            int width = LoWord(lParam);
            int height = HiWord(lParam);
            if (width > 0 && height > 0)
                ClientSizeChanged?.Invoke(width, height);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            // HwndHost が BuildWindowCore 後に子 HWND をサブクラス化して WndProc(オーバーライド) にメッセージを流すため、
            // クラスの WndProc 自体は既定処理（DefWindowProc）で足りる。
            _wndProcHolder = StaticWndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcHolder),
                hInstance = GetModuleHandle(null),
                // クラス名は登録が生きている間（プロセス寿命）保持する必要があるため解放しない（登録は 1 回のみ）。
                lpszClassName = Marshal.StringToHGlobalUni(ClassName),
                // 背景ブラシ無し: スワップチェーンが全面を塗るので WM_ERASEBKGND での塗り潰しを避けてちらつきを防ぐ。
                hbrBackground = IntPtr.Zero,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"映像ウィンドウクラスの登録に失敗しました（Win32 error={Marshal.GetLastWin32Error()}）");
            _classRegistered = true;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hwnd, msg, wParam, lParam);

    private static int LoWord(IntPtr value) => unchecked((short)((long)value & 0xFFFF));

    private static int HiWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xFFFF));
}
