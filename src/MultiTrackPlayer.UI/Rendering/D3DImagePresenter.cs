using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D9 = Vortice.Direct3D9;

namespace MultiTrackPlayer.UI.Rendering;

/// <summary>
/// D3D9Ex↔D3D11 共有サーフェス経由で WPF の <see cref="D3DImage"/> に映像を表示するブリッジ。
/// フレームの受け渡し形態（<see cref="FrameKind"/>）に応じて 2 経路を持つ:
///
/// <list type="bullet">
/// <item><see cref="FrameKind.Gpu"/>（GPU ゼロコピー）: エンジンが GPU 上で BGRA 変換済みの共有テクスチャを、
/// その共有ハンドルから D3D9 サーフェスとして開いて <see cref="D3DImage"/> のバックバッファへ直結する。CPU コピーを一切行わない。</item>
/// <item><see cref="FrameKind.Cpu"/>（SW フォールバック）: ネイティブ BGRA バッファを Staging テクスチャへ行単位で
/// アップロードし、共有テクスチャへ GPU コピーして表示する（従来経路）。</item>
/// </list>
///
/// スレッド契約: 生成・<see cref="Present"/>・<see cref="Dispose"/> はすべて UI スレッド上で呼ぶこと。
/// MediaEngine.TryGetFrame / ReturnFrame の UI スレッド専有契約に合わせ、Present 内で同期的に提示を
/// 完了させる（フレームを非同期に保持しない＝リング枯渇を招かない）。
/// </summary>
public sealed class D3DImagePresenter : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    // D3D11 の Dynamic テクスチャは B8G8R8A8_UNorm、D3D9 側は対応する A8R8G8B8 で共有サーフェスを開く。
    private const Format SharedFormat = Format.B8G8R8A8_UNorm;
    private const D9.Format D9SharedFormat = D9.Format.A8R8G8B8;

    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private D9.IDirect3D9Ex? _d3d9;
    private D9.IDirect3DDevice9Ex? _d3d9Device;
    private ID3D11Device? _d3d11Device;
    private ID3D11DeviceContext? _context;

    private ID3D11Texture2D? _uploadTexture;
    private ID3D11Texture2D? _sharedTexture;
    private D9.IDirect3DTexture9? _d3d9Texture;
    private D9.IDirect3DSurface9? _d3d9Surface;

    private int _width;
    private int _height;
    private bool _deviceLost;

    // GPU 経路用。エンジンのスロット共有ハンドルごとに開いた D3D9 サーフェス（=StretchRect のコピー元）をキャッシュする
    // （リングは 4 スロットなので通常 4 ハンドル。解像度変化時にまとめて破棄して開き直す）。
    private int _gpuWidth;
    private int _gpuHeight;
    private readonly Dictionary<IntPtr, (D9.IDirect3DTexture9 Texture, D9.IDirect3DSurface9 Surface)> _gpuSurfaces = new();
    // D3DImage のバックバッファに固定する 1 枚の RenderTarget（=StretchRect のコピー先）。SetBackBuffer は生成時に一度だけ呼ぶ。
    private D9.IDirect3DSurface9? _gpuBackBuffer;

    /// <summary>WPF 側で <c>Image.Source</c> にバインドする描画先。<see cref="Present"/> のたびに中身を更新する。</summary>
    public D3DImage D3DImage { get; } = new();

    public D3DImagePresenter()
    {
        InitDevices();
    }

    private void InitDevices()
    {
        IntPtr hwnd = GetDesktopWindow();

        // ① 実スワップチェーンを持たないウィンドウレス提示用の D3D9Ex デバイスを生成する。
        _d3d9 = D9.D3D9.Direct3DCreate9Ex();
        var pp = new D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = D9.SwapEffect.Discard,
            DeviceWindowHandle = hwnd,
            PresentationInterval = D9.PresentInterval.Immediate,
            BackBufferFormat = D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };
        _d3d9Device = _d3d9.CreateDeviceEx(
            0,
            D9.DeviceType.Hardware,
            hwnd,
            D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
            pp);

        // ② D3D9 が使うアダプタの LUID を取得し、③ 同一 LUID の DXGI アダプタで D3D11 デバイスを作る。
        // 同一アダプタでないと共有テクスチャのオープンに失敗するため LUID を突き合わせる。
        // D3D9 の Luid（Vortice.Direct3D9.Luid）と DXGI の Luid（Vortice.Luid）は別型のため値で比較する。
        D9.Luid d9Luid = _d3d9.GetAdapterLuid(0);
        IDXGIAdapter1? adapter = FindAdapterByLuid(d9Luid);

        // BgraSupport は B8G8R8A8 共有テクスチャの作成に必須。指定が無いと Map/共有時にクラッシュする。
        const DeviceCreationFlags creationFlags = DeviceCreationFlags.BgraSupport;
        try
        {
            // アダプタを明示指定するときは DriverType.Unknown を渡す規約。見つからなければ既定アダプタで妥協する。
            if (adapter != null)
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, creationFlags, FeatureLevels,
                    out _d3d11Device, out _context).CheckError();
            }
            else
            {
                DiagnosticLog.Write("d3dPresenter", "同一 LUID の DXGI アダプタが見つからず既定アダプタで生成");
                D3D11.D3D11CreateDevice(null, DriverType.Hardware, creationFlags, FeatureLevels,
                    out _d3d11Device, out _context).CheckError();
            }
        }
        finally
        {
            adapter?.Dispose();
        }

        _deviceLost = false;
        DiagnosticLog.Write("d3dPresenter", "デバイス初期化完了");
    }

    private IDXGIAdapter1? FindAdapterByLuid(D9.Luid target)
    {
        IDXGIFactory1? factory = null;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            {
                AdapterDescription1 desc = adapter.Description1;
                if (desc.Luid.LowPart == target.LowPart && desc.Luid.HighPart == target.HighPart)
                    return adapter;
                adapter.Dispose();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("d3dPresenter", $"DXGI アダプタ列挙に失敗: {ex.Message}");
        }
        finally
        {
            factory?.Dispose();
        }
        return null;
    }

    private void CreateOrResizeSurfaces(int width, int height)
    {
        ReleaseSurfaces();

        _width = width;
        _height = height;

        // アップロード用: CPU から毎フレーム書き込む Staging テクスチャ（Map(Write) 対象）。
        // Dynamic は BindFlags に最低1つ（ShaderResource 等）を要求し、BindFlags.None では
        // CreateTexture2D が E_INVALIDARG で失敗する。バインド不要な Staging を使う
        // （Staging は Map でき、CopyResource のコピー元にもなれる）。
        var uploadDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharedFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };
        _uploadTexture = _d3d11Device!.CreateTexture2D(in uploadDesc);

        // 共有用: D3D9 と共有する RenderTarget テクスチャ（MiscFlags=Shared）。CopyResource のコピー先。
        var sharedDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharedFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };
        _sharedTexture = _d3d11Device.CreateTexture2D(in sharedDesc);

        // 共有ハンドルを取り出し、同じサーフェスを D3D9 側テクスチャとして開く。
        IntPtr sharedHandle;
        using (IDXGIResource dxgiResource = _sharedTexture.QueryInterface<IDXGIResource>())
        {
            sharedHandle = dxgiResource.SharedHandle;
        }

        IntPtr openHandle = sharedHandle;
        _d3d9Texture = _d3d9Device!.CreateTexture(
            (uint)width, (uint)height, 1, D9.Usage.RenderTarget, D9SharedFormat, D9.Pool.Default, ref openHandle);
        _d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);

        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
        D3DImage.Unlock();

        DiagnosticLog.Write("d3dPresenter", $"サーフェス生成 {width}x{height}");
    }

    /// <summary>1 フレームを表示する。<see cref="FrameKind"/> に応じて GPU ゼロコピー経路か CPU 経路へ振り分ける。呼び出し直後に <c>ReturnFrame</c> できるよう同期完結する。</summary>
    public void Present(VideoFrameLease lease)
    {
        if (lease.Kind == FrameKind.Gpu)
            PresentGpu(lease);
        else
            PresentCpu(lease);
    }

    /// <summary>
    /// GPU 経路。エンジンが GPU 上で BGRA 変換済みの共有テクスチャ（<paramref name="lease"/> の共有ハンドル）から、
    /// 固定バックバッファへ GPU 内 <c>StretchRect</c> でコピーして表示する。CPU コピーを行わない。
    ///
    /// バックバッファは 1 枚に固定し <see cref="D3DImage.SetBackBuffer"/> は生成時のみ呼ぶ。毎フレームの
    /// SetBackBuffer 切替（WPF のコンポジションリソース載せ替え）を避け、UI スレッドの提示コストを抑える。
    /// </summary>
    private void PresentGpu(VideoFrameLease lease)
    {
        if (_deviceLost)
        {
            TryRecoverDevices();
            if (_deviceLost) return;
        }

        // フロントバッファが利用不可（ロック画面・RDP 切断等）の間は描画をスキップする。
        if (!D3DImage.IsFrontBufferAvailable) return;

        try
        {
            // 解像度が変わったら旧サーフェス群は無効（エンジン側もスロットのテクスチャを作り直す）。まとめて破棄し、
            // 固定バックバッファを作り直して SetBackBuffer し直す。
            if (_gpuBackBuffer == null || _gpuWidth != lease.Width || _gpuHeight != lease.Height)
            {
                ReleaseGpuSurfaces();
                CreateGpuBackBuffer(lease.Width, lease.Height);
                _gpuWidth = lease.Width;
                _gpuHeight = lease.Height;
            }

            D9.IDirect3DSurface9? source = GetOrOpenSharedSurface(lease.SharedSurfaceHandle, lease.Width, lease.Height);
            if (source == null) return; // オープン失敗（ログ済み）。次フレームで再試行。

            // 当該スロットの共有サーフェス → 固定バックバッファへ GPU 内コピー（CPU 不介入）。同解像度なので拡縮なし。
            // 全面矩形（LTRB=0,0,w,h）を渡す。同サイズのため XYWH 解釈でも同値。
            var full = new D9.Rect(0, 0, lease.Width, lease.Height);
            _d3d9Device!.StretchRect(source, full, _gpuBackBuffer!, full, D9.TextureFilter.None);

            // バックバッファは固定のまま。dirty 通知だけで WPF に再描画を促す（SetBackBuffer 切替はしない）。
            D3DImage.Lock();
            D3DImage.AddDirtyRect(new Int32Rect(0, 0, lease.Width, lease.Height));
            D3DImage.Unlock();
        }
        catch (Exception ex)
        {
            _deviceLost = true;
            DiagnosticLog.Write("d3dPresenter", $"GPU 提示失敗（デバイスロストとして扱う）: {ex.Message}");
        }
    }

    /// <summary>D3DImage のバックバッファに固定する RenderTarget を 1 枚生成し、SetBackBuffer で一度だけ結びつける。</summary>
    private void CreateGpuBackBuffer(int width, int height)
    {
        _gpuBackBuffer = _d3d9Device!.CreateRenderTarget(
            (uint)width, (uint)height, D9SharedFormat, D9.MultisampleType.None, 0, lockable: false);

        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _gpuBackBuffer.NativePointer);
        D3DImage.Unlock();
        DiagnosticLog.Write("d3dPresenter", $"GPU バックバッファ生成 {width}x{height}");
    }

    /// <summary>共有ハンドルに対応する D3D9 サーフェスを取得する（未オープンなら開いてキャッシュ）。オープンできなければ null。</summary>
    private D9.IDirect3DSurface9? GetOrOpenSharedSurface(IntPtr sharedHandle, int width, int height)
    {
        if (sharedHandle == IntPtr.Zero) return null;
        if (_gpuSurfaces.TryGetValue(sharedHandle, out var cached)) return cached.Surface;

        try
        {
            // 別 D3D11 デバイス（エンジンの GpuDeviceContext）が作った共有テクスチャを、同一アダプタの D3D9 側で開く。
            IntPtr openHandle = sharedHandle;
            D9.IDirect3DTexture9 texture = _d3d9Device!.CreateTexture(
                (uint)width, (uint)height, 1, D9.Usage.RenderTarget, D9SharedFormat, D9.Pool.Default, ref openHandle);
            D9.IDirect3DSurface9 surface = texture.GetSurfaceLevel(0);
            _gpuSurfaces[sharedHandle] = (texture, surface);
            DiagnosticLog.Write("d3dPresenter", $"共有サーフェスを開いた {width}x{height} cache={_gpuSurfaces.Count}");
            return surface;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("d3dPresenter", $"共有サーフェスのオープンに失敗: {ex.Message}");
            return null;
        }
    }

    private void ReleaseGpuSurfaces()
    {
        foreach (var entry in _gpuSurfaces.Values)
        {
            entry.Surface.Dispose();
            entry.Texture.Dispose();
        }
        _gpuSurfaces.Clear();
        _gpuBackBuffer?.Dispose();
        _gpuBackBuffer = null;
        _gpuWidth = 0;
        _gpuHeight = 0;
    }

    /// <summary>CPU 経路（SW フォールバック）。ネイティブ BGRA バッファを Staging→共有テクスチャへコピーして表示する。</summary>
    private unsafe void PresentCpu(VideoFrameLease lease)
    {
        if (_deviceLost)
        {
            TryRecoverDevices();
            if (_deviceLost) return;
        }

        // フロントバッファが利用不可（ロック画面・RDP 切断等）の間は描画をスキップする。
        if (!D3DImage.IsFrontBufferAvailable) return;

        try
        {
            // サーフェス生成も try 内に置き、初回生成の失敗（E_INVALIDARG 等）でクラッシュせず
            // デバイスロスト扱いにして次フレームの復旧に委ねる。
            if (_uploadTexture == null || _width != lease.Width || _height != lease.Height)
                CreateOrResizeSurfaces(lease.Width, lease.Height);

            // ① Staging テクスチャを Map(Write)。RowPitch は Stride と一致するとは限らない。
            MappedSubresource mapped = _context!.Map(_uploadTexture!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);

            // ② 行単位 memcpy。Stride と RowPitch のずれを前提に必ず 1 行ずつコピーする。
            int rowBytes = Math.Min(lease.Stride, (int)mapped.RowPitch);
            byte* src = (byte*)lease.PixelBuffer;
            byte* dst = (byte*)mapped.DataPointer;
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(src + (long)y * lease.Stride, dst + (long)y * mapped.RowPitch, mapped.RowPitch, rowBytes);

            // ③ Unmap → ④ 共有テクスチャへ GPU コピー → ⑤ Flush で D3D9 側から見えるよう確定。
            _context.Unmap(_uploadTexture!, 0);
            _context.CopyResource(_sharedTexture!, _uploadTexture!);
            _context.Flush();

            // ⑥ 全面を dirty にして WPF に再描画を促す。
            D3DImage.Lock();
            D3DImage.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            D3DImage.Unlock();
        }
        catch (Exception ex)
        {
            // デバイスロスト系（GPU リセット・ドライバ更新等）は次フレームでの復旧に委ねる。
            _deviceLost = true;
            DiagnosticLog.Write("d3dPresenter", $"Present 失敗（デバイスロストとして扱う）: {ex.Message}");
        }
    }

    private void TryRecoverDevices()
    {
        try
        {
            ReleaseSurfaces();
            // GPU 経路のサーフェスは旧 D3D9 デバイス上にあるため、デバイス作り直し前に破棄して次フレームで開き直させる。
            ReleaseGpuSurfaces();
            ReleaseDevices();
            InitDevices();
            _width = 0; // 次の Present で必ずサーフェスを作り直す
            _height = 0;
            DiagnosticLog.Write("d3dPresenter", "デバイス復旧成功");
        }
        catch (Exception ex)
        {
            _deviceLost = true;
            DiagnosticLog.Write("d3dPresenter", $"デバイス復旧失敗: {ex.Message}");
        }
    }

    private void ReleaseSurfaces()
    {
        _d3d9Surface?.Dispose();
        _d3d9Surface = null;
        _d3d9Texture?.Dispose();
        _d3d9Texture = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _uploadTexture?.Dispose();
        _uploadTexture = null;
    }

    private void ReleaseDevices()
    {
        _context?.Dispose();
        _context = null;
        _d3d11Device?.Dispose();
        _d3d11Device = null;
        _d3d9Device?.Dispose();
        _d3d9Device = null;
        _d3d9?.Dispose();
        _d3d9 = null;
    }

    public void Dispose()
    {
        // WPF から共有サーフェスの参照を外してから COM を下位（サーフェス）→上位（デバイス）の順で解放する。
        try
        {
            D3DImage.Lock();
            D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            D3DImage.Unlock();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("d3dPresenter", $"BackBuffer 解除に失敗: {ex.Message}");
        }

        ReleaseSurfaces();
        ReleaseGpuSurfaces();
        ReleaseDevices();
    }
}
