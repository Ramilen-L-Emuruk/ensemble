using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

public unsafe class AudioDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private SwrContext* _swrCtx;
    private readonly AVRational _timeBase;

    public int StreamIndex { get; }
    public const int OutSampleRate = 44100;
    public const int OutChannels = 2;
    public static readonly AVSampleFormat OutFormat = AVSampleFormat.Flt;

    public AudioDecoder(AVStream* stream)
    {
        StreamIndex = stream->index;
        _timeBase = stream->time_base;

        var codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Audio codec not found");

        _ctx = avcodec_alloc_context3(codec);
        avcodec_parameters_to_context(_ctx, stream->codecpar);
        int ret = avcodec_open2(_ctx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"Could not open audio codec: {ret}");
    }

    public byte[]? DecodePacket(AVPacket* pkt)
    {
        int ret = avcodec_send_packet(_ctx, pkt);
        if (ret < 0) return null;
        using var frame = new FrameHolder();
        ret = avcodec_receive_frame(_ctx, frame.Frame);
        if (ret < 0) return null;
        return Resample(frame.Frame);
    }

    private byte[]? Resample(AVFrame* frame)
    {
        EnsureSwrContext(frame);

        long delay = swr_get_delay(_swrCtx, frame->sample_rate);
        int outSamples = (int)av_rescale_rnd(delay + frame->nb_samples,
            OutSampleRate, frame->sample_rate, AVRounding.Up);

        int bufSize = outSamples * OutChannels * sizeof(float);
        var buf = new byte[bufSize];
        int actualSamples;

        fixed (byte* dstPtr = buf)
        {
            byte* outPtr = dstPtr;
            actualSamples = swr_convert(_swrCtx, &outPtr, outSamples,
                                        frame->extended_data, frame->nb_samples);
        }

        if (actualSamples < 0) return null;
        int actualBytes = actualSamples * OutChannels * sizeof(float);
        if (actualBytes < bufSize)
        {
            var result = new byte[actualBytes];
            Array.Copy(buf, result, actualBytes);
            return result;
        }
        return buf;
    }

    private void EnsureSwrContext(AVFrame* frame)
    {
        if (_swrCtx != null) return;

        AVChannelLayout outLayout = default;
        av_channel_layout_default(&outLayout, OutChannels);
        AVChannelLayout inLayout = frame->ch_layout;

        SwrContext* ctx = null;
        swr_alloc_set_opts2(&ctx,
            &outLayout, OutFormat, OutSampleRate,
            &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
            0, null);
        swr_init(ctx);
        _swrCtx = ctx;

        av_channel_layout_uninit(&outLayout);
    }

    public void FlushBuffers() => avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        if (_swrCtx != null) { SwrContext* s = _swrCtx; swr_free(&s); _swrCtx = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; avcodec_free_context(&c); _ctx = null; }
    }

    private unsafe class FrameHolder : IDisposable
    {
        public AVFrame* Frame = av_frame_alloc();
        public void Dispose() { if (Frame != null) { AVFrame* f = Frame; av_frame_free(&f); } Frame = null; }
    }
}