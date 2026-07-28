using System.Runtime.InteropServices;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MultiTrackPlayer.Engine.Rendering;

/// <summary>
/// 入力フレームの色空間情報。判定不能時は BT.709 / Limited（デフォルト）を使う。
/// </summary>
/// <param name="IsBt709">true=BT.709、false=BT.601 の色変換マトリクス。</param>
/// <param name="IsFullRange">true=フルレンジ(0-255)、false=リミテッドレンジ(16-235) の YCbCr。</param>
public readonly record struct ColorInfo(bool IsBt709, bool IsFullRange)
{
    /// <summary>判定不能時のデフォルト（BT.709 / Limited）。</summary>
    public static readonly ColorInfo Default = new(IsBt709: true, IsFullRange: false);
}

/// <summary>
/// ハードウェアデコード出力（NV12/P010 の D3D11 テクスチャ配列）を <see cref="ID3D11VideoProcessor"/> で
/// BGRA 共有テクスチャへ GPU 上で色変換（YCbCr→RGB）する変換器。<see cref="GpuDeviceContext"/> の
/// <see cref="GpuDeviceContext.VideoDevice"/>/<see cref="GpuDeviceContext.VideoContext"/> を用いる。
///
/// enumerator / processor は入出力サイズ確定時（初回・サイズ変化時）に生成する。入力 InputView は
/// ArraySlice（=サブリソース番号）ごとにキャッシュし、入力テクスチャのポインタが変わったらキャッシュを破棄する。
///
/// スレッド契約: <see cref="ConvertInto"/> は単一のデコードスレッドから呼ぶ前提（内部にロックは持たない）。
/// 生成・<see cref="Dispose"/> は所有者（<c>MediaEngine</c>）のスレッドで行い、デコードスレッドの開始前の生成・
/// Join 後の破棄という happens-before で安全性を担保する（ConvertInto を呼ぶスレッドとは別スレッド）。
/// </summary>
public sealed class GpuFrameConverter : IDisposable
{
    private readonly GpuDeviceContext _gpu;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _width;
    private int _height;

    // 入力テクスチャ配列は同一ポインタが使い回される前提。ポインタが変わったら InputView キャッシュを破棄する。
    private ID3D11Texture2D? _inputTexture;
    private IntPtr _inputTexturePtr;
    private readonly Dictionary<uint, ID3D11VideoProcessorInputView> _inputViews = new();

    // 直近に設定した色空間。変化時のみ再設定してドライバ呼び出しとログを抑える。
    private ColorInfo? _lastColor;

    public GpuFrameConverter(GpuDeviceContext gpu)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        if (_gpu.VideoDevice == null || _gpu.VideoContext == null)
        {
            // この環境では VideoProcessor 経路を使えない。呼び出し側（次ステップ）が従来経路へフォールバックする想定。
            DiagnosticLog.Write("gpuConvert",
                $"VideoDevice/VideoContext が無い環境です（videoDevice={(_gpu.VideoDevice != null ? "有" : "無")} " +
                $"videoContext={(_gpu.VideoContext != null ? "有" : "無")}）。ConvertInto は例外になります");
        }
    }

    /// <summary>
    /// 入力テクスチャ（HW デコード出力）を色変換し、<paramref name="ring"/> の指定スロットの共有テクスチャへ Blt する。
    /// enumerator/processor は必要なら生成し、InputView は ArraySlice ごとにキャッシュする。
    /// </summary>
    /// <param name="inputTexturePtr">HW デコード出力の <see cref="ID3D11Texture2D"/> の生ポインタ（AVFrame.data[0]）。</param>
    /// <param name="subResourceIndex">テクスチャ配列内の ArraySlice 番号（AVFrame.data[1]）。</param>
    /// <param name="width">動画の幅。</param>
    /// <param name="height">動画の高さ。</param>
    /// <param name="color">入力の色空間。</param>
    /// <param name="ring">出力先リング。</param>
    /// <param name="slotIndex">出力先スロット。</param>
    public void ConvertInto(IntPtr inputTexturePtr, int subResourceIndex, int width, int height,
        ColorInfo color, GpuVideoFrameRing ring, int slotIndex)
    {
        var videoContext = _gpu.VideoContext
            ?? throw new InvalidOperationException("VideoContext が無い環境のため GPU 色変換を実行できません");

        EnsureProcessor(width, height);
        ring.SetEnumerator(_enumerator!);

        var inputView = GetOrCreateInputView(inputTexturePtr, (uint)subResourceIndex);
        var outputView = ring.GetOutputView(slotIndex);

        ApplyColorSpaces(color);

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };
        videoContext.VideoProcessorBlt(_processor!, outputView, outputFrame: 0, new[] { stream }).CheckError();

        // 共有テクスチャへの書き込みを別デバイスから確実に読めるようフラッシュする。
        _gpu.ImmediateContext.Flush();
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_enumerator != null && _processor != null && _width == width && _height == height)
            return;

        DisposeProcessor();
        // サイズが変わると入力テクスチャ配列も別物になるため InputView キャッシュも破棄する。
        DisposeInputViews();

        var videoDevice = _gpu.VideoDevice
            ?? throw new InvalidOperationException("VideoDevice が無い環境のため VideoProcessor を生成できません");

        var desc = new VideoProcessorContentDescription
        {
            Usage = VideoUsage.PlaybackNormal,
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Rational(1, 1),
            OutputFrameRate = new Rational(1, 1),
        };
        _enumerator = videoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = videoDevice.CreateVideoProcessor(_enumerator, rateConversionIndex: 0);
        _width = width;
        _height = height;
        _lastColor = null; // processor 再生成後は色空間を再設定させる。

        DiagnosticLog.Write("gpuConvert", $"VideoProcessor 生成 size={width}x{height}");
    }

    private ID3D11VideoProcessorInputView GetOrCreateInputView(IntPtr inputTexturePtr, uint arraySlice)
    {
        if (inputTexturePtr != _inputTexturePtr)
        {
            DisposeInputViews();
            // FFmpeg 所有のテクスチャを借用ラップする。Dispose 時に解放されるよう AddRef で参照を1つ足しておき、
            // 破棄時の Release とバランスさせる（FFmpeg 側の参照カウントを不正に減らさないため）。
            _inputTexture = new ID3D11Texture2D(inputTexturePtr);
            Marshal.AddRef(inputTexturePtr);
            _inputTexturePtr = inputTexturePtr;
        }

        if (_inputViews.TryGetValue(arraySlice, out var cached))
            return cached;

        var desc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = arraySlice },
        };
        var view = _gpu.VideoDevice!.CreateVideoProcessorInputView(_inputTexture!, _enumerator!, desc);
        _inputViews[arraySlice] = view;
        DiagnosticLog.Write("gpuConvert", $"InputView 生成 arraySlice={arraySlice}");
        return view;
    }

    private void ApplyColorSpaces(ColorInfo color)
    {
        if (_lastColor is ColorInfo prev && prev == color) return;

        // D3D11_VIDEO_PROCESSOR_COLOR_SPACE 相当。入力は YCbCr（Matrix と Nominal_Range が効く）。
        // Nominal_Range: 1=16-235(Limited)、2=0-255(Full)。
        var input = new VideoProcessorColorSpace
        {
            Usage = 0, // 0=Playback
            RGB_Range = 0,
            YCbCr_Matrix = color.IsBt709 ? 1u : 0u, // 0=BT.601, 1=BT.709
            YCbCr_xvYCC = 0,
            Nominal_Range = color.IsFullRange ? 2u : 1u,
        };
        _gpu.VideoContext!.VideoProcessorSetStreamColorSpace(_processor!, streamIndex: 0, input);

        // 出力は RGB フルレンジ(0-255)。
        var output = new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0, // 0=Full(0-255)
            YCbCr_Matrix = color.IsBt709 ? 1u : 0u,
            YCbCr_xvYCC = 0,
            Nominal_Range = 2u, // 0-255
        };
        _gpu.VideoContext!.VideoProcessorSetOutputColorSpace(_processor!, output);

        _lastColor = color;
        DiagnosticLog.Write("gpuConvert",
            $"色空間設定 input(matrix={(color.IsBt709 ? "BT709" : "BT601")} range={(color.IsFullRange ? "Full" : "Limited")}) output(RGB Full)");
    }

    public void Dispose()
    {
        DisposeInputViews();
        DisposeProcessor();
    }

    private void DisposeProcessor()
    {
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _lastColor = null;
    }

    private void DisposeInputViews()
    {
        foreach (var view in _inputViews.Values)
            view.Dispose();
        _inputViews.Clear();

        if (_inputTexture != null)
        {
            // 借用ラップを Dispose すると、生成時に足した AddRef 分の参照が Release される。
            _inputTexture.Dispose();
            _inputTexture = null;
        }
        _inputTexturePtr = IntPtr.Zero;
    }
}
