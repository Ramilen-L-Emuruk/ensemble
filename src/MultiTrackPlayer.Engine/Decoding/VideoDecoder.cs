using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;
using System.Runtime.InteropServices;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

public unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVBufferRef* _hwCtx;
    private SwsContext* _swsCtx;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private bool _useHw;

    public int StreamIndex => _streamIndex;

    public VideoDecoder(AVStream* stream)
    {
        _streamIndex = stream->index;
        _timeBase = stream->time_base;

        var codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Video codec not found");

        _ctx = avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("Could not allocate video codec context");

        avcodec_parameters_to_context(_ctx, stream->codecpar);

        _hwCtx = HardwareAccel.TryCreateD3D11VAContext();
        if (_hwCtx != null)
        {
            _ctx->hw_device_ctx = av_buffer_ref(_hwCtx);
            _useHw = true;
        }

        int ret = avcodec_open2(_ctx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"Could not open video codec: {ret}");
    }

    public VideoFrameData? DecodePacket(AVPacket* pkt)
    {
        int ret = avcodec_send_packet(_ctx, pkt);
        if (ret < 0) return null;
        using var frame = new FrameHolder();
        ret = avcodec_receive_frame(_ctx, frame.Frame);
        if (ret < 0) return null;
        return ConvertFrame(frame.Frame);
    }

    private VideoFrameData? ConvertFrame(AVFrame* srcFrame)
    {
        AVFrame* frame = srcFrame;
        AVFrame* swFrame = null;
        try
        {
            if (_useHw && frame->format == (int)AVPixelFormat.D3d11)
            {
                swFrame = av_frame_alloc();
                swFrame->format = (int)AVPixelFormat.Nv12;
                int ret = av_hwframe_transfer_data(swFrame, frame, 0);
                if (ret < 0) return null;
                frame = swFrame;
            }

            int w = frame->width;
            int h = frame->height;
            var srcFmt = (AVPixelFormat)frame->format;

            _swsCtx = sws_getCachedContext(
                _swsCtx, w, h, srcFmt,
                w, h, AVPixelFormat.Bgra,
                2, null, null, null); // 2 = SWS_BILINEAR

            var pixels = new byte[w * h * 4];

            var srcDataArr = new byte*[] {
                (byte*)frame->data[0], (byte*)frame->data[1], (byte*)frame->data[2], (byte*)frame->data[3],
                null, null, null, null
            };
            var srcStride = new int[] {
                frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3],
                0, 0, 0, 0
            };

            fixed (byte* dstPtr = pixels)
            {
                var dstData = new byte*[] { dstPtr, null, null, null, null, null, null, null };
                var dstStride = new int[] { w * 4, 0, 0, 0, 0, 0, 0, 0 };
                sws_scale(_swsCtx, srcDataArr, srcStride, 0, h, dstData, dstStride);
            }

            long pts = srcFrame->best_effort_timestamp;
            double seconds = pts * av_q2d(_timeBase);
            return new VideoFrameData(pixels, w, h, TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            if (swFrame != null) av_frame_free(&swFrame);
        }
    }

    public void FlushBuffers() => avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        if (_swsCtx != null) { sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_hwCtx != null) { AVBufferRef* h = _hwCtx; av_buffer_unref(&h); _hwCtx = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; avcodec_free_context(&c); _ctx = null; }
    }

    private unsafe class FrameHolder : IDisposable
    {
        public AVFrame* Frame = av_frame_alloc();
        public void Dispose() { if (Frame != null) { AVFrame* f = Frame; av_frame_free(&f); } Frame = null; }
    }
}