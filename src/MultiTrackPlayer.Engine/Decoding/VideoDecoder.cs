using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

public unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVBufferRef* _hwCtx;
    // 注意: sws_scale_frame + threads によるマルチスレッド変換は FFmpeg 6.0 では使えない。
    // buf 未割当の dst フレーム（外部プールバッファを data に直指定）に対して、エラーではなく
    // 「内部バッファを新規確保して成功を返す」ため、プールには何も書かれず黒画面になる
    // （実機検証で確認済み）。単スレッド sws_scale が正しい経路。
    private SwsContext* _swsCtx;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private bool _useHw;
    // get_format コールバックの実体。ネイティブ側から関数ポインタ経由で呼ばれるため、
    // GC に回収されないようインスタンスの生存期間中フィールドで保持する。
    private AVCodecContext_get_format? _getFormatDelegate;

    public int StreamIndex => _streamIndex;

    /// <param name="stream">対象の映像ストリーム。</param>
    /// <param name="sharedDevicePtr">描画と共有する自前 <c>ID3D11Device</c> の生ポインタ。
    /// <see cref="IntPtr.Zero"/> の場合は従来どおり FFmpeg に D3D11VA デバイスを自前生成させる。</param>
    public VideoDecoder(AVStream* stream, IntPtr sharedDevicePtr = default)
    {
        _streamIndex = stream->index;
        _timeBase = stream->time_base;

        var codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Video codec not found");

        _ctx = avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("Could not allocate video codec context");

        avcodec_parameters_to_context(_ctx, stream->codecpar);

        // 共有デバイスが渡されていれば注入版を優先。失敗（null）や未指定なら従来の自前生成へフォールバックする。
        bool usedSharedDevice = false;
        if (sharedDevicePtr != IntPtr.Zero)
        {
            _hwCtx = HardwareAccel.CreateD3D11VAContextFromDevice(sharedDevicePtr);
            usedSharedDevice = _hwCtx != null;
        }
        if (_hwCtx == null)
            _hwCtx = HardwareAccel.TryCreateD3D11VAContext();

        DiagnosticLog.Write("gpuDevice",
            $"VideoDecoder HW デバイス経路: {(usedSharedDevice ? "自前デバイス注入版" : "FFmpeg 自前生成版(従来)")} " +
            $"(hwCtx={(_hwCtx != null ? "有効" : "無効=SW")})");

        if (_hwCtx != null)
        {
            _ctx->hw_device_ctx = av_buffer_ref(_hwCtx);
            _useHw = true;

            // HW デコードが実際に選択されるかを診断するため get_format を差し込む。
            // 候補フォーマットをログし、D3D11 が提示されればそれを選ぶ（無ければ SW フォールバック）。
            _getFormatDelegate = SelectPixelFormat;
            _ctx->get_format = _getFormatDelegate;
        }

        int ret = avcodec_open2(_ctx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"Could not open video codec: {ret}");
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
            DiagnosticLog.Write("error", $"映像デコードエラー ret={ret} ({FFmpegError.Describe(ret)})");
        return false;
    }

    /// <summary>
    /// デコーダが提示するピクセルフォーマット候補から使用するものを選ぶ get_format コールバック。
    /// 候補と選択結果を診断ログに残し、D3D11 が提示されれば HW デコード経路を維持する。
    /// D3D11 が候補になければソフトウェアデコードにフォールバックする。
    /// </summary>
    private AVPixelFormat SelectPixelFormat(AVCodecContext* ctx, AVPixelFormat* fmt)
    {
        var offered = new List<AVPixelFormat>();
        for (AVPixelFormat* p = fmt; *p != AVPixelFormat.None; p++)
            offered.Add(*p);

        bool hasD3d11 = offered.Contains(AVPixelFormat.D3d11);
        AVPixelFormat selected = hasD3d11
            ? AVPixelFormat.D3d11
            : (offered.Count > 0 ? offered[0] : AVPixelFormat.None);

        DiagnosticLog.Write("hwDecode",
            $"get_format 候補=[{string.Join(", ", offered)}] 選択={selected} " +
            $"({(hasD3d11 ? "D3D11=HWデコード有効" : "D3D11非提示=SWフォールバック")})");

        return selected;
    }

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

    public void FlushBuffers() => avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        if (_swsCtx != null) { sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_hwCtx != null) { AVBufferRef* h = _hwCtx; av_buffer_unref(&h); _hwCtx = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; avcodec_free_context(&c); _ctx = null; }
    }
}
