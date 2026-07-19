using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;
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

    // sws_scale_frame + threads オプションによるマルチスレッド変換（P6）。
    // 一度でも失敗したら以降は単スレッド sws_scale へ恒久的にフォールバックする。
    private SwsContext* _mtSwsCtx;
    private bool _mtSwsFailed;
    private int _mtSwsW, _mtSwsH;
    private AVPixelFormat _mtSwsSrcFmt;

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

    /// <summary>デコーダへパケットを送る（pkt に null を渡すと EOF フラッシュ）。avcodec_send_packet の戻り値をそのまま返す。</summary>
    public int SendPacket(AVPacket* pkt) => avcodec_send_packet(_ctx, pkt);

    /// <summary>1フレーム分だけ受信する。EAGAIN/EOF なら false（呼び出し側はループを抜ける）。</summary>
    public bool TryReceiveFrame(AVFrame* frame) => avcodec_receive_frame(_ctx, frame) == 0;

    public double GetPtsSeconds(AVFrame* frame)
    {
        long pts = frame->best_effort_timestamp;
        if (pts == long.MinValue) pts = 0; // AV_NOPTS_VALUE
        return Math.Max(0.0, pts * av_q2d(_timeBase));
    }

    /// <summary>
    /// D3D11VA サーフェスなら CPU へ転送し、BGRA へ変換して dst（少なくとも frame の幅×高さ×4 バイト確保済み）へ書き込む。
    /// 呼び出し側のプールバッファへ直接書くため、このメソッド自体は byte[] を確保しない。
    /// </summary>
    public bool ConvertInto(AVFrame* srcFrame, IntPtr dst, int dstStride, out int width, out int height)
    {
        width = 0;
        height = 0;
        AVFrame* frame = srcFrame;
        AVFrame* swFrame = null;
        try
        {
            if (_useHw)
            {
                var fmt = (AVPixelFormat)frame->format;
                // D3D11VAのハードウェアサーフェス形式はCPUから直接アクセスできないため転送が必要
                if (fmt == AVPixelFormat.D3d11 || fmt == AVPixelFormat.D3d11vaVld)
                {
                    swFrame = av_frame_alloc();
                    int ret = av_hwframe_transfer_data(swFrame, frame, 0);
                    if (ret < 0) return false;
                    frame = swFrame;
                }
                // else: コーデックがソフトウェアデコードにフォールバック済みのため直接使用
            }

            int w = frame->width;
            int h = frame->height;
            if (w <= 0 || h <= 0) return false;

            var srcFmt = (AVPixelFormat)frame->format;

            if (!_mtSwsFailed && TryConvertMultiThreaded(frame, w, h, srcFmt, dst, dstStride))
            {
                width = w;
                height = h;
                return true;
            }

            _swsCtx = sws_getCachedContext(
                _swsCtx, w, h, srcFmt,
                w, h, AVPixelFormat.Bgra,
                2, null, null, null); // 2 = SWS_BILINEAR

            if (_swsCtx == null) return false;

            var srcDataArr = new byte*[] {
                (byte*)frame->data[0], (byte*)frame->data[1], (byte*)frame->data[2], (byte*)frame->data[3],
                null, null, null, null
            };
            var srcStride = new int[] {
                frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3],
                0, 0, 0, 0
            };
            var dstData = new byte*[] { (byte*)dst, null, null, null, null, null, null, null };
            var dstStrideArr = new int[] { dstStride, 0, 0, 0, 0, 0, 0, 0 };
            sws_scale(_swsCtx, srcDataArr, srcStride, 0, h, dstData, dstStrideArr);

            width = w;
            height = h;
            return true;
        }
        finally
        {
            if (swFrame != null) av_frame_free(&swFrame);
        }
    }

    /// <summary>
    /// sws_alloc_context + threads オプション + sws_scale_frame によるスライス並列変換を試みる。
    /// dst フレームの data/linesize は呼び出し側プールバッファへ直接セットし、buf は未割当のままにする
    /// （av_frame_free 時に data 側は解放されない＝プールバッファの所有権は移らない）。
    /// FFmpeg 側がこの「外部バッファへの書き込み」を受け付けない環境では失敗を検知し、
    /// 以降は恒久的に単スレッド sws_scale へフォールバックする。
    /// </summary>
    private bool TryConvertMultiThreaded(AVFrame* frame, int w, int h, AVPixelFormat srcFmt, IntPtr dst, int dstStride)
    {
        if (_mtSwsCtx == null || _mtSwsW != w || _mtSwsH != h || _mtSwsSrcFmt != srcFmt)
        {
            if (_mtSwsCtx != null) { sws_freeContext(_mtSwsCtx); _mtSwsCtx = null; }

            _mtSwsCtx = sws_alloc_context();
            if (_mtSwsCtx == null) { _mtSwsFailed = true; return false; }

            void* ctxPtr = _mtSwsCtx;
            av_opt_set_int(ctxPtr, "srcw", w, 0);
            av_opt_set_int(ctxPtr, "srch", h, 0);
            av_opt_set_int(ctxPtr, "src_format", (long)srcFmt, 0);
            av_opt_set_int(ctxPtr, "dstw", w, 0);
            av_opt_set_int(ctxPtr, "dsth", h, 0);
            av_opt_set_int(ctxPtr, "dst_format", (long)AVPixelFormat.Bgra, 0);
            av_opt_set_int(ctxPtr, "sws_flags", 2, 0); // SWS_BILINEAR
            av_opt_set_int(ctxPtr, "threads", 0, 0);   // 0 = 自動（CPUコア数に応じてスライス分割）

            if (sws_init_context(_mtSwsCtx, null, null) < 0)
            {
                sws_freeContext(_mtSwsCtx);
                _mtSwsCtx = null;
                _mtSwsFailed = true;
                return false;
            }
            _mtSwsW = w;
            _mtSwsH = h;
            _mtSwsSrcFmt = srcFmt;
        }

        AVFrame* dstFrame = av_frame_alloc();
        if (dstFrame == null) return false;
        try
        {
            dstFrame->format = (int)AVPixelFormat.Bgra;
            dstFrame->width = w;
            dstFrame->height = h;
            dstFrame->data[0] = dst;
            dstFrame->linesize[0] = dstStride;

            int ret = sws_scale_frame(_mtSwsCtx, dstFrame, frame);
            if (ret < 0) { _mtSwsFailed = true; return false; }
            return true;
        }
        finally
        {
            av_frame_free(&dstFrame);
        }
    }

    public void FlushBuffers() => avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        if (_swsCtx != null) { sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_mtSwsCtx != null) { sws_freeContext(_mtSwsCtx); _mtSwsCtx = null; }
        if (_hwCtx != null) { AVBufferRef* h = _hwCtx; av_buffer_unref(&h); _hwCtx = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; avcodec_free_context(&c); _ctx = null; }
    }
}
