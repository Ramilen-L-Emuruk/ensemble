using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Video;
using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>
/// CPU 経路の書き込み。HW サーフェスなら CPU へ転送し、sws_scale で BGRA 化して
/// <see cref="VideoFrameRing"/> のネイティブバッファへ書く（従来経路）。
/// </summary>
internal sealed unsafe class CpuFrameSink : IVideoFrameSink
{
    private readonly VideoDecoder _decoder;
    private readonly VideoFrameRing _ring;

    public CpuFrameSink(VideoDecoder decoder, VideoFrameRing ring)
    {
        _decoder = decoder;
        _ring = ring;
    }

    public int BeginWrite(int width, int height, SeekEpoch epoch) => _ring.BeginWrite(width, height, epoch);

    public bool WriteFrame(AVFrame* frame, int slotIndex)
    {
        IntPtr dst = _ring.GetWriteBuffer(slotIndex);
        int stride = frame->width * 4;
        return _decoder.ConvertInto(frame, dst, stride, out _, out _);
    }

    public void CommitWrite(int slotIndex, double ptsSeconds) => _ring.CommitWrite(slotIndex, ptsSeconds);

    public void AbortWrite(int slotIndex) => _ring.AbortWrite(slotIndex);

    public void MarkEof() => _ring.MarkEof();
}
