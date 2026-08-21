using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

public unsafe class AudioDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private SwrContext* _swrCtx;
    private readonly AVRational _timeBase;

    /// <summary>
    /// <c>_swrCtx</c>・<c>_playbackSpeed</c>・<c>_swrSpeed</c>・<c>_isResamplerBroken</c> を保護する。
    /// 再生速度の変更は UI スレッドから（キー・メニュー・ComboBox の全経路）、リサンプルは
    /// 音声デコードスレッドから来る。保護しないと、速度変更が解放したポインタをデコード側が
    /// 読む use-after-free になる（ネイティブメモリ破壊）。
    /// </summary>
    private readonly object _swrLock = new();

    public int StreamIndex { get; }
    // OBS 等の一般的なソースは 48kHz のため、無意味なリサンプルを避けるべく出力もネイティブに合わせる
    public const int OutSampleRate = 48000;
    public const int OutChannels = 2;
    public static readonly AVSampleFormat OutFormat = AVSampleFormat.Flt;

    private double _playbackSpeed = 1.0;
    /// <summary>現在の <c>_swrCtx</c> を構築したときの再生速度。<c>_playbackSpeed</c> と食い違ったら作り直す。</summary>
    private double _swrSpeed = double.NaN;
    private bool _isResamplerBroken;

    // 再生速度を変更すると SWR コンテキストを再初期化して有効出力レートを調整する
    // speed 2.0 → effectiveOutRate = 24000 → 1 source秒あたり半数サンプル → WASAPI が 0.5 秒で消費 → 2x 速再生
    //
    // ここでは値を記録するだけで SwrContext には触らない。実際の作り直しは次のリサンプル時に
    // デコードスレッドが行う。ネイティブ資源の確保・解放をデコードスレッドへ一本化することで、
    // UI スレッドが解放したポインタをデコード側が読む事故を構造的に防ぐ
    public double PlaybackSpeed
    {
        get { lock (_swrLock) return _playbackSpeed; }
        set
        {
            double clamped = Math.Clamp(value, 0.1, 4.0);
            lock (_swrLock)
            {
                if (_playbackSpeed == clamped) return;
                _playbackSpeed = clamped;
                // 速度は SwrContext の構築パラメータの一部。パラメータが変わった以上、
                // 前回の初期化失敗を引きずらずに作り直しを試みる
                _isResamplerBroken = false;
            }
        }
    }

    public int EffectiveOutSampleRate
    {
        get { lock (_swrLock) return EffectiveOutSampleRateUnlocked; }
    }

    /// <summary><c>_swrLock</c> を保持している間だけ使う内部版。</summary>
    private int EffectiveOutSampleRateUnlocked => (int)(OutSampleRate / _playbackSpeed);

    /// <summary>
    /// リサンプラの初期化に失敗し、このデコーダが音声を出力できない状態。呼び出し側は
    /// このトラックを EOF 扱いにして畳むこと。放置すると <c>MultiTrackMixer</c> の
    /// 共通利用可能量が 0 に固定され、健全な他トラックまで無音になる。
    /// 再生速度を変更すると作り直しを試みるため、この状態は解除されうる。
    /// </summary>
    public bool IsResamplerBroken
    {
        get { lock (_swrLock) return _isResamplerBroken; }
    }

    public AudioDecoder(AVStream* stream)
    {
        StreamIndex = stream->index;
        _timeBase = stream->time_base;

        var codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Audio codec not found");

        _ctx = avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("Could not allocate audio codec context");

        // ここから先で例外を投げると、確保済みの _ctx を呼び出し元が解放できず恒久リークになる
        //（コンストラクタが失敗するとインスタンスが返らないため、呼び出し元は Dispose を呼べない）
        try
        {
            int paramRet = avcodec_parameters_to_context(_ctx, stream->codecpar);
            if (paramRet < 0)
                throw new InvalidOperationException(
                    $"Could not copy audio codec parameters: {paramRet} ({FFmpegError.Describe(paramRet)})");

            int ret = avcodec_open2(_ctx, codec, null);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"Could not open audio codec: {ret} ({FFmpegError.Describe(ret)})");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>デコーダへパケットを送る（pkt に null を渡すと EOF フラッシュ）。avcodec_send_packet の戻り値をそのまま返す。</summary>
    public int SendPacket(AVPacket* pkt) => avcodec_send_packet(_ctx, pkt);

    /// <summary>1フレーム分だけ受信する。EAGAIN/EOF なら false（呼び出し側はループを抜ける）。
    /// それ以外の負値（壊れたデータ等の本当のデコードエラー）は診断ログに残す。</summary>
    public bool TryReceiveFrame(AVFrame* frame)
    {
        int ret = avcodec_receive_frame(_ctx, frame);
        if (ret == 0) return true;
        if (ret != -EAGAIN && ret != AVERROR_EOF)
            DiagnosticLog.Write("error", $"音声デコードエラー ret={ret} ({FFmpegError.Describe(ret)})");
        return false;
    }

    public double GetPtsSeconds(AVFrame* frame)
    {
        long pts = frame->pts;
        if (pts == long.MinValue) pts = frame->best_effort_timestamp;
        if (pts == long.MinValue) return double.NaN; // AV_NOPTS_VALUE
        return pts * av_q2d(_timeBase);
    }

    public int NbSamples(AVFrame* frame) => frame->nb_samples;
    public int InSampleRate(AVFrame* frame) => frame->sample_rate;

    /// <summary>
    /// デコード済みフレームを EffectiveOutSampleRate/OutChannels/OutFormat へリサンプルする。
    /// 失敗した場合は null を返す。リサンプラ自体が使えなくなった（＝以降も出力できない）かは
    /// <see cref="IsResamplerBroken"/> で判別すること。
    /// </summary>
    public byte[]? ResampleFrame(AVFrame* frame) => Resample(frame);

    private byte[]? Resample(AVFrame* frame)
    {
        byte[]? pcm;
        string? brokenReason;
        lock (_swrLock)
        {
            pcm = ResampleLocked(frame, out brokenReason);
        }

        // WriteFatal は（デバッグモード無効時の既定経路で）プロセス間ミューテックスの待機を伴い、
        // 最大 500ms 級でブロックする。_swrLock を保持したまま呼ぶと、同じロックを取る
        // PlaybackSpeed セッター（＝UI スレッドからの速度変更操作）がその間固まるため、
        // 記録はロックを抜けてから行う
        if (brokenReason != null)
            DiagnosticLog.WriteFatal("audio", $"音声リサンプラを無効化 stream={StreamIndex}: {brokenReason}");
        return pcm;
    }

    /// <summary>
    /// <c>_swrLock</c> を保持した状態で呼ぶこと。リサンプラが恒久的に使えなくなった場合は、
    /// その理由を <paramref name="brokenReason"/> に入れて返す（記録は呼び出し側がロック外で行う）。
    /// </summary>
    private byte[]? ResampleLocked(AVFrame* frame, out string? brokenReason)
    {
        if (!EnsureSwrContext(frame, out brokenReason)) return null;

        long delay = swr_get_delay(_swrCtx, frame->sample_rate);
        // 換算先は SwrContext に設定した出力レート（= OutSampleRate / speed）。定数 OutSampleRate を
        // 使うと speed < 1.0 のときバッファが足りず、出力しきれなかった分が swr_convert の
        // 内部バッファへ持ち越されて遅延が積む
        int outSamples = (int)av_rescale_rnd(delay + frame->nb_samples,
            EffectiveOutSampleRateUnlocked, frame->sample_rate, AVRounding.Up);

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

    /// <summary>
    /// 現在の再生速度に対応する SwrContext を用意する。<c>_swrLock</c> を保持して呼ぶこと。
    /// </summary>
    /// <returns>使用可能な SwrContext がある場合 true。確保・初期化に失敗した場合 false。</returns>
    private bool EnsureSwrContext(AVFrame* frame, out string? brokenReason)
    {
        brokenReason = null;
        if (_isResamplerBroken) return false;
        if (_swrCtx != null && _swrSpeed == _playbackSpeed) return true;

        // 速度変更を受けての作り直し。SwrContext の解放地点はここと Dispose の 2 箇所だけに保つ
        FreeSwrContext();

        AVChannelLayout outLayout = default;
        av_channel_layout_default(&outLayout, OutChannels);
        AVChannelLayout inLayout = frame->ch_layout;

        // effectiveOutRate = OutSampleRate / speed で SWR に出力密度を伝える
        // → WASAPI は OutSampleRate で消費するので speed 倍の速度で再生される
        int effectiveOutRate = EffectiveOutSampleRateUnlocked;
        SwrContext* ctx = null;
        try
        {
            // 戻り値を捨てると、初期化に失敗した SwrContext で swr_convert が負値を返し続け、
            // このトラックが「無音のまま EOF にもならない」状態で居座る。ミキサーはそれを
            // 除外できないため、健全な他トラックまで巻き込んで無音になる
            int optsRet = swr_alloc_set_opts2(&ctx,
                &outLayout, OutFormat, effectiveOutRate,
                &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                0, null);
            if (optsRet < 0 || ctx == null)
            {
                string detail = $"ret={optsRet} ({FFmpegError.Describe(optsRet)}) ctxNull={ctx == null}";
                // swr_alloc_set_opts2 は確保直後に *ps へ代入してからチャンネルレイアウト等を
                // 検証するため、検証で失敗しても ctx には確保済みコンテキストが入っている。
                // 解放しないと、壊れた音声トラックを持つファイルを開くたびにネイティブメモリが積む
                if (ctx != null) swr_free(&ctx);
                _isResamplerBroken = true;
                brokenReason = $"SwrContext を確保できない {detail}";
                return false;
            }

            int initRet = swr_init(ctx);
            if (initRet < 0)
            {
                swr_free(&ctx);
                _isResamplerBroken = true;
                brokenReason =
                    $"SwrContext を初期化できない ret={initRet} ({FFmpegError.Describe(initRet)}) " +
                    $"inRate={frame->sample_rate} inFormat={(AVSampleFormat)frame->format} outRate={effectiveOutRate}";
                return false;
            }

            _swrCtx = ctx;
            _swrSpeed = _playbackSpeed;
            return true;
        }
        finally
        {
            // 失敗して return する経路でも解放する必要があるため finally に置く
            av_channel_layout_uninit(&outLayout);
        }
    }

    /// <summary><c>_swrLock</c> を保持して呼ぶこと。</summary>
    private void FreeSwrContext()
    {
        if (_swrCtx == null) return;
        SwrContext* s = _swrCtx;
        swr_free(&s);
        _swrCtx = null;
        _swrSpeed = double.NaN;
    }

    public void FlushBuffers() => avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        lock (_swrLock) FreeSwrContext();
        if (_ctx != null) { AVCodecContext* c = _ctx; avcodec_free_context(&c); _ctx = null; }
    }
}
