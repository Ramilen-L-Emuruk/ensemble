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

    private double _playbackSpeed = 1.0;
    // 再生速度を変更すると SWR コンテキストを再初期化して有効出力レートを調整する
    // speed 2.0 → effectiveOutRate = 22050 → 1 source秒あたり半数サンプル → WASAPI が 0.5 秒で消費 → 2x 速再生
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            double clamped = Math.Clamp(value, 0.1, 4.0);
            if (_playbackSpeed == clamped) return;
            _playbackSpeed = clamped;
            if (_swrCtx != null) { SwrContext* s = _swrCtx; swr_free(&s); _swrCtx = null; }
        }
    }

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

        // 1パケットから複数フレームが出るコーデックに対応するためループで受け取る
        var segments = new List<byte[]>();
        AVFrame* frame = av_frame_alloc();
        try
        {
            while (avcodec_receive_frame(_ctx, frame) == 0)
            {
                var pcm = Resample(frame);
                if (pcm != null) segments.Add(pcm);
                av_frame_unref(frame);
            }
        }
        finally
        {
            av_frame_free(&frame);
        }

        if (segments.Count == 0) return null;
        if (segments.Count == 1) return segments[0];

        int total = segments.Sum(s => s.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var seg in segments) { Buffer.BlockCopy(seg, 0, result, offset, seg.Length); offset += seg.Length; }
        return result;
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

        // effectiveOutRate = OutSampleRate / speed で SWR に出力密度を伝える
        // → WASAPI は OutSampleRate で消費するので speed 倍の速度で再生される
        int effectiveOutRate = (int)(OutSampleRate / _playbackSpeed);
        SwrContext* ctx = null;
        swr_alloc_set_opts2(&ctx,
            &outLayout, OutFormat, effectiveOutRate,
            &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
            0, null);
        swr_init(ctx);
        _swrCtx = ctx;

        av_channel_layout_uninit(&outLayout);
    }

    public void FlushBuffers()
    {
        avcodec_flush_buffers(_ctx);
        if (_swrCtx != null)
            swr_set_compensation(_swrCtx, 0, OutSampleRate); // フラッシュ時はドリフト補正をリセット
    }

    // swr_set_compensation を使ったソフト補正（VLC aout_FiltersAdjustResampling 相当）
    // sampleDelta > 0 → サンプル追加（masterClock 前進を速める）→ 映像先行ドリフト補正
    // sampleDelta < 0 → サンプル削除（masterClock 前進を遅らせる）→ 映像遅延ドリフト補正
    public void SetDriftCompensation(int sampleDelta, int compensationDistance)
    {
        if (_swrCtx == null) return;
        swr_set_compensation(_swrCtx, sampleDelta, compensationDistance);
    }

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