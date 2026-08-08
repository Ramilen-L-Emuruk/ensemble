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
/// HW デコード出力テクスチャはコーデックのアライメントに切り上げたサイズで確保されるため、実映像より
/// 大きいことがある。processor 生成時にソース矩形を実映像サイズへ固定して、パディング領域を切り落とす。
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
    /// <param name="width">動画の幅（入力テクスチャから切り出すソース矩形の幅も兼ねる）。</param>
    /// <param name="height">動画の高さ（入力テクスチャから切り出すソース矩形の高さも兼ねる）。</param>
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

        // HW デコード出力テクスチャは、コーデックが要求するアライメントへ切り上げたサイズで確保される
        // （FFmpeg の D3D11VA は AV1/HEVC で 128 アライメント。1080p なら実体は 1920x1152）。
        // VideoProcessorBlt の既定のソース矩形は「入力サーフェス全体」なので、指定しないとパディング行まで
        // 変換対象になり、下端に帯が出た上に映像が縦へ圧縮される。実映像の矩形を明示して切り出す。
        // 出力先はちょうど width x height なので DestRect は既定と同じ値だが、ドライバ既定に依存しないよう明示する。
        // これらは processor のストリーム状態として永続するため、Blt ごとではなく生成時に一度だけ設定すればよい。
        var frameRect = new Vortice.RawRect(0, 0, width, height);
        var videoContext = _gpu.VideoContext!;
        videoContext.VideoProcessorSetStreamSourceRect(_processor, streamIndex: 0, enable: true, frameRect);
        videoContext.VideoProcessorSetStreamDestRect(_processor, streamIndex: 0, enable: true, frameRect);

        DiagnosticLog.Write("gpuConvert", $"VideoProcessor 生成 size={width}x{height}（source/dest rect を実映像サイズへ固定）");
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

            // 実テクスチャはアライメント切り上げで映像サイズより大きいことがある（AV1 1080p なら 1920x1152）。
            // ソース矩形でどれだけ切り落としているかを実測で追えるよう、テクスチャ差し替え時に一度だけ残す。
            var texDesc = _inputTexture.Description;
            DiagnosticLog.Write("gpuConvert",
                $"入力テクスチャ {texDesc.Width}x{texDesc.Height} / 映像 {_width}x{_height}" +
                $"（差分は HW デコーダのアライメント padding。ソース矩形で除外する）");
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
