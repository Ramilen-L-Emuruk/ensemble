using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

public unsafe class AudioDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private SwrContext* _swrCtx;
    private readonly AVRational _timeBase;

    public int StreamIndex { get; }
    // OBS 等の一般的なソースは 48kHz のため、無意味なリサンプルを避けるべく出力もネイティブに合わせる
    public const int OutSampleRate = 48000;
    public const int OutChannels = 2;
    public static readonly AVSampleFormat OutFormat = AVSampleFormat.Flt;

    private double _playbackSpeed = 1.0;
    // 再生速度を変更すると SWR コンテキストを再初期化して有効出力レートを調整する
    // speed 2.0 → effectiveOutRate = 24000 → 1 source秒あたり半数サンプル → WASAPI が 0.5 秒で消費 → 2x 速再生
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

    public int EffectiveOutSampleRate => (int)(OutSampleRate / _playbackSpeed);

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

    /// <summary>デコーダへパケットを送る（pkt に null を渡すと EOF フラッシュ）。avcodec_send_packet の戻り値をそのまま返す。</summary>
    public int SendPacket(AVPacket* pkt) => avcodec_send_packet(_ctx, pkt);

    /// <summary>1フレーム分だけ受信する。EAGAIN/EOF なら false（呼び出し側はループを抜ける）。</summary>
    public bool TryReceiveFrame(AVFrame* frame) => avcodec_receive_frame(_ctx, frame) == 0;

    public double GetPtsSeconds(AVFrame* frame)
    {
        long pts = frame->pts;
        if (pts == long.MinValue) pts = frame->best_effort_timestamp;
        if (pts == long.MinValue) return double.NaN; // AV_NOPTS_VALUE
        return pts * av_q2d(_timeBase);
    }

    public int NbSamples(AVFrame* frame) => frame->nb_samples;
    public int InSampleRate(AVFrame* frame) => frame->sample_rate;

    /// <summary>デコード済みフレームを OutSampleRate/OutChannels/OutFormat へリサンプルする。</summary>
    public byte[]? ResampleFrame(AVFrame* frame) => Resample(frame);

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
        int effectiveOutRate = EffectiveOutSampleRate;
        SwrContext* ctx = null;
        swr_alloc_set_opts2(&ctx,
            &outLayout, OutFormat, effectiveOutRate,
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
}
