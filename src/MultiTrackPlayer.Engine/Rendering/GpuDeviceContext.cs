using MultiTrackPlayer.Engine.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace MultiTrackPlayer.Engine.Rendering;

/// <summary>
/// GPU ゼロコピー描画 Sub-phase 2.1（デバイス統一）の土台。描画・色変換・FFmpeg デコードで共有する
/// 単一の <see cref="ID3D11Device"/> を自前で生成して保持する。
///
/// この段階では「自前デバイスを 1 つ作り、その生ポインタを FFmpeg のデコードに注入できる状態」を用意する
/// だけで、色変換（sws_scale）や <c>ID3D11VideoProcessor</c> の実処理はまだ行わない（後続 2.2 以降）。
/// <see cref="VideoDevice"/> / <see cref="VideoContext"/> は後続フェーズで使うため生成のみ行う。
///
/// スレッド契約: 生成・<see cref="Dispose"/> は所有者（<c>MediaEngine</c>）のスレッドで呼ぶ。デバイス自体は
/// FFmpeg のデコードスレッドと跨って使われうるため、生成直後に <c>ID3D11Multithread.SetMultithreadProtected(true)</c>
/// を有効化してマルチスレッドアクセスの保険をかける。
/// </summary>
public sealed class GpuDeviceContext : IDisposable
{
    // BgraSupport=D3D9 共有テクスチャ（B8G8R8A8）用、VideoSupport=後続の ID3D11VideoProcessor 用。
    private const DeviceCreationFlags CreationFlags =
        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private ID3D11Device _device;
    private ID3D11DeviceContext _immediateContext;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11Multithread? _multithread;

    /// <summary>描画・デコードで共有する D3D11 デバイス。</summary>
    public ID3D11Device Device => _device;

    /// <summary>即時（immediate）デバイスコンテキスト。</summary>
    public ID3D11DeviceContext ImmediateContext => _immediateContext;

    /// <summary>後続 2.2 の色変換で使う VideoDevice（取得できない環境では null）。</summary>
    public ID3D11VideoDevice? VideoDevice => _videoDevice;

    /// <summary>後続 2.2 の色変換で使う VideoContext（取得できない環境では null）。</summary>
    public ID3D11VideoContext? VideoContext => _videoContext;

    /// <summary>FFmpeg（AVD3D11VADeviceContext.device）へ注入するための <see cref="ID3D11Device"/> の生ポインタ。</summary>
    public IntPtr NativeDevicePointer => _device.NativePointer;

    public GpuDeviceContext()
    {
        // アダプタ未指定（null）+ DriverType.Hardware で既定のハードウェアアダプタからデバイスを作る。
        // 失敗時は例外を投げ、呼び出し側（MediaEngine）が従来の FFmpeg 自前デバイス経路へフォールバックする。
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, CreationFlags, FeatureLevels,
            out _device!, out _immediateContext!).CheckError();

        FeatureLevel level = _device.FeatureLevel;

        // 後続 2.2 で使う VideoDevice / VideoContext を取得（この環境で無ければ null のまま続行）。
        _videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = _immediateContext.QueryInterfaceOrNull<ID3D11VideoContext>();

        // FFmpeg と同一デバイスをスレッド跨ぎで使うための保険。取得できない環境でも握りつぶさずログして続行する。
        _multithread = _immediateContext.QueryInterfaceOrNull<ID3D11Multithread>();
        if (_multithread != null)
            _multithread.SetMultithreadProtected(true);
        else
            DiagnosticLog.Write("gpuDevice", "ID3D11Multithread を QueryInterface できず（マルチスレッド保護なしで続行）");

        DiagnosticLog.Write("gpuDevice",
            $"自前 D3D11 デバイス生成成功 featureLevel={level} " +
            $"videoDevice={(_videoDevice != null ? "有" : "無")} videoContext={(_videoContext != null ? "有" : "無")}");
    }

    public void Dispose()
    {
        // COM を下位（VideoContext/VideoDevice/Multithread）→ immediate context → device の順で解放する。
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _multithread?.Dispose();
        _multithread = null;
        _immediateContext?.Dispose();
        _immediateContext = null!;
        _device?.Dispose();
        _device = null!;
    }
}
