using System.Runtime.InteropServices;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MultiTrackPlayer.Engine.Rendering;

/// <summary>
/// 映像子ウィンドウ（HWND）に D3D11 スワップチェーンを張り、GPU リングのスロットテクスチャを
/// バックバッファへコピーして vsync Present する（案Y・段階1）。D3DImage / DWM 合成を経由しないため、
/// 提示レートが vsync（frame latency waitable object）で安定し、CompositionTarget.Rendering のジッタに
/// 起因するフレーム間引きが原理的に消える。
///
/// バックバッファは映像サイズで確保し、ウィンドウへの表示は <see cref="Scaling.Stretch"/> に委ねる
/// （段階1 ではアスペクト維持のレターボックスは未実装。段階2 以降で VideoProcessor 経由に置き換える）。
///
/// スレッド契約: 生成・<see cref="Dispose"/> は所有スレッド（MediaEngine）から。<see cref="WaitForVBlank"/>・
/// <see cref="Render"/>・<see cref="Present"/> は vout スレッドから呼ぶ。D3D11 デバイスは
/// <see cref="GpuDeviceContext"/>（SetMultithreadProtected(true)）を共有する。
/// </summary>
public sealed class SwapChainVideoPresenter : IDisposable
{
    private const int BufferCount = 2;

    private readonly GpuDeviceContext _gpu;
    private IDXGISwapChain2 _swapChain;
    private readonly IntPtr _waitableHandle;

    public SwapChainVideoPresenter(GpuDeviceContext gpu, IntPtr hwnd, int videoWidth, int videoHeight)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));

        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)videoWidth,
            Height = (uint)videoHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        using var swapChain1 = factory.CreateSwapChainForHwnd(_gpu.Device, hwnd, desc);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain2>();

        // 1フレームだけ先行させる（低レイテンシ）。waitable object で vsync ごとに 1 回だけ起床する。
        _swapChain.MaximumFrameLatency = 1;
        _waitableHandle = _swapChain.FrameLatencyWaitableObject;

        // DXGI 既定の Alt+Enter フルスクリーン遷移は無効化（フルスクリーンは WPF 側で制御する）。
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        DiagnosticLog.Write("d3dPresenter", $"swapchain 生成 {videoWidth}x{videoHeight}");
    }

    /// <summary>次の提示可能タイミング（vsync）まで待つ。UI 合成に依存しない安定した提示ペースを得るための待機点。</summary>
    public void WaitForVBlank()
    {
        if (_waitableHandle != IntPtr.Zero)
            WaitForSingleObjectEx(_waitableHandle, 1000, false);
    }

    /// <summary>リングのスロットテクスチャ（BGRA・映像サイズ）をバックバッファへコピーする。</summary>
    public void Render(GpuVideoFrameRing ring, int slotIndex)
    {
        ID3D11Texture2D? src = ring.GetSlotTexture(slotIndex);
        if (src == null) return;

        // FLIP モデルでは Present ごとにバックバッファが入れ替わるため、コピー直前に index 0 を取得する。
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _gpu.ImmediateContext.CopyResource(backBuffer, src);
    }

    /// <summary>vsync 同期で提示する。</summary>
    public void Present() => _swapChain.Present(1, PresentFlags.None);

    public void Dispose()
    {
        _swapChain?.Dispose();
        _swapChain = null!;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObjectEx(IntPtr hHandle, uint dwMilliseconds, bool bAlertable);
}
