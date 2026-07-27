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
/// GPU ゼロコピー描画 Phase 1。エンジンのネイティブ BGRA フレームを D3D11 の Dynamic テクスチャに
/// 行単位でアップロードし、Shared テクスチャ経由で D3D9Ex サーフェスへ渡して WPF の <see cref="D3DImage"/>
/// に表示する。CPU 側での中間 <c>byte[]</c> 確保や <c>WriteableBitmap.WritePixels</c> を回避する。
///
/// スレッド契約: 生成・<see cref="Present"/>・<see cref="Dispose"/> はすべて UI スレッド上で呼ぶこと。
/// MediaEngine.TryGetFrame / ReturnFrame の UI スレッド専有契約に合わせ、Present 内で同期的にコピーを
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

    /// <summary>1 フレームを GPU へアップロードして表示する。呼び出し直後に <c>ReturnFrame</c> できるよう同期完結する。</summary>
    public unsafe void Present(VideoFrameLease lease)
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
        ReleaseDevices();
    }
}
