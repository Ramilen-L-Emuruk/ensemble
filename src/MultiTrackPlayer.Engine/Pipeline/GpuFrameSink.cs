using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Rendering;
using MultiTrackPlayer.Engine.Video;
using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>
/// GPU ゼロコピー経路の書き込み。HW デコード出力の D3D11 テクスチャを <see cref="GpuFrameConverter"/> で
/// BGRA 共有テクスチャへ GPU 上で色変換して <see cref="GpuVideoFrameRing"/> のスロットへ Blt する。
/// CPU 転送（av_hwframe_transfer_data）も sws_scale も経ない。
/// </summary>
internal sealed unsafe class GpuFrameSink : IVideoFrameSink
{
    private readonly VideoDecoder _decoder;
    private readonly GpuVideoFrameRing _ring;
    private readonly GpuFrameConverter _converter;

    public GpuFrameSink(VideoDecoder decoder, GpuVideoFrameRing ring, GpuFrameConverter converter)
    {
        _decoder = decoder;
        _ring = ring;
        _converter = converter;
    }

    public int BeginWrite(int width, int height) => _ring.BeginWrite(width, height);

    public bool WriteFrame(AVFrame* frame, int slotIndex)
    {
        if (!_decoder.TryGetHwTexture(frame, out IntPtr texturePtr, out int subResource, out var color))
        {
            // IsHardwareAccelerated だが実際には SW フレームが届いた（稀: D3D11 非対応コーデック等）。安全に破棄する。
            DiagnosticLog.Write("gpuConvert",
                $"HW テクスチャ取得失敗 fmt={(AVPixelFormat)frame->format}（GPU 経路に SW フレーム到達。破棄）");
            return false;
        }

        try
        {
            _converter.ConvertInto(texturePtr, subResource, frame->width, frame->height, color, _ring, slotIndex);
            return true;
        }
        catch (Exception ex)
        {
            // ドライバ都合等で Blt が失敗しても、デコードスレッドを落とさず1フレーム破棄で凌ぐ。
            DiagnosticLog.Write("gpuConvert", $"GPU 変換失敗（1フレーム破棄）: {ex.Message}");
            return false;
        }
    }

    public void CommitWrite(int slotIndex, double ptsSeconds) => _ring.CommitWrite(slotIndex, ptsSeconds);

    public void AbortWrite(int slotIndex) => _ring.AbortWrite(slotIndex);

    public void Flush() => _ring.Flush();

    public void MarkEof() => _ring.MarkEof();
}
