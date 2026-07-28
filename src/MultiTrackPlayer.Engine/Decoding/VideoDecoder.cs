using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Rendering;
using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// HW デコード経路（D3D11VA デバイスコンテキスト注入済み）か。true の場合、デコード出力は D3D11 テクスチャで
    /// 得られ、<see cref="TryGetHwTexture"/> でゼロコピー描画へ渡せる。false の場合は CPU 経路（<see cref="ConvertInto"/>）を使う。
    /// </summary>
    public bool IsHardwareAccelerated => _useHw;

    /// <param name="stream">対象の映像ストリーム。</param>
    /// <param name="sharedHwDeviceCtx">全デコーダで共有する FFmpeg D3D11VA デバイスコンテキスト
    /// （<c>av_buffer_ref</c> で参照を1つ取る）。null の場合は従来どおり FFmpeg に D3D11VA デバイスを自前生成させる。</param>
    public VideoDecoder(AVStream* stream, AVBufferRef* sharedHwDeviceCtx = null)
    {
        _streamIndex = stream->index;
        _timeBase = stream->time_base;

        var codec = PickBestDecoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Video codec not found");

        _ctx = avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("Could not allocate video codec context");

        avcodec_parameters_to_context(_ctx, stream->codecpar);

        // このデコーダが D3D11VA ハードウェアデコード（hw_device_ctx 注入方式）に対応するかをデコード前に確定させる。
        // get_format は最初のフレームをデコードした時点で初めて呼ばれるため、Open 時の経路選択（GPU/CPU）に間に合わない。
        // avcodec_get_hw_config はデコード前に列挙でき、対応が無ければ最初から SW（CPU 経路）へ確実にフォールバックできる。
        // （AV1 等、この環境の FFmpeg が D3D11VA 候補を出さないコーデックで映像が黒画面になる問題への対処）
        bool supportsD3d11va = SupportsD3d11vaDecode(codec);

        // D3D11VA 対応時のみ HW デバイスを注入する。共有 HW デバイスコンテキストが渡されていれば参照共有版を優先し、
        // 未指定（null）や失敗時は従来の自前生成へフォールバックする。
        bool usedSharedDevice = false;
        if (supportsD3d11va)
        {
            if (sharedHwDeviceCtx != null)
            {
                // 共有コンテキストへの参照を1つ取るだけ（av_hwdevice_ctx_init は呼ばない）。ファイル切替ごとに
                // init/uninit を繰り返すとネイティブヒープを破損させ、連続 D&D でクラッシュしていたための共有化。
                _hwCtx = av_buffer_ref(sharedHwDeviceCtx);
                usedSharedDevice = _hwCtx != null;
            }
            if (_hwCtx == null)
                _hwCtx = HardwareAccel.TryCreateD3D11VAContext();
        }

        DiagnosticLog.Write("gpuDevice",
            $"VideoDecoder HW デバイス経路: d3d11vaSupported={supportsD3d11va} " +
            $"{(usedSharedDevice ? "自前デバイス注入版" : (_hwCtx != null ? "FFmpeg 自前生成版(従来)" : "SW デコード（CPU 経路）"))} " +
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

    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX（FFmpeg）。hw_device_ctx 注入方式で HW デコードできることを示すビット。
    private const int HwConfigMethodHwDeviceCtx = 0x01;

    /// <summary>
    /// 指定コーデックが D3D11VA ハードウェアデコード（hw_device_ctx 注入方式）に対応するかを、デコード前に
    /// <c>avcodec_get_hw_config</c> の列挙で判定する。対応が無ければ HW デバイスを注入せず SW デコード（CPU 経路）になる。
    /// </summary>
    private static bool SupportsD3d11vaDecode(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = avcodec_get_hw_config(codec, i);
            if (cfg == null) break;
            if (cfg->device_type == AVHWDeviceType.D3d11va &&
                (cfg->methods & HwConfigMethodHwDeviceCtx) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 指定 codec_id のデコーダを選ぶ。既定デコーダが D3D11VA 非対応の場合、同じ codec_id で D3D11VA に対応する
    /// 別のデコーダを優先する（例: AV1 で既定が SW の libdav1d のとき、D3D11VA 対応の内蔵 av1 デコーダへ切り替える）。
    /// これにより既定が SW デコーダのコーデックでもハードウェアデコード経路へ載せられる。対応デコーダが無ければ既定を返す。
    /// </summary>
    private static AVCodec* PickBestDecoder(AVCodecID codecId)
    {
        AVCodec* def = avcodec_find_decoder(codecId);
        if (def == null) return null;
        if (SupportsD3d11vaDecode(def)) return def;

        // 既定が D3D11VA 非対応。全デコーダを走査し、同じ codec_id で D3D11VA 対応のものがあれば優先する。
        void* iter = null;
        for (AVCodec* c = av_codec_iterate(&iter); c != null; c = av_codec_iterate(&iter))
        {
            if (c->id == codecId && av_codec_is_decoder(c) != 0 && SupportsD3d11vaDecode(c))
            {
                DiagnosticLog.Write("hwDecode",
                    $"D3D11VA 対応デコーダへ切替: {GetCodecName(def)} → {GetCodecName(c)} (codec_id={codecId})");
                return c;
            }
        }
        return def;
    }

    private static string GetCodecName(AVCodec* codec)
        => codec != null && codec->name != null ? Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "?" : "?";

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
    /// HW デコード出力フレームから D3D11 テクスチャ（と色空間情報）を取り出す。GPU ゼロコピー描画経路で使う。
    /// D3D11VA サーフェスでない（＝SW フォールバック等）場合は false を返す。
    /// </summary>
    /// <param name="frame">デコード済みフレーム。</param>
    /// <param name="texturePtr">D3D11 テクスチャ配列（<c>ID3D11Texture2D*</c>）の生ポインタ。</param>
    /// <param name="subResourceIndex">テクスチャ配列内の ArraySlice 番号。</param>
    /// <param name="color">入力の色空間（YCbCr マトリクス・レンジ）。</param>
    /// <returns>D3D11 テクスチャを取り出せたら true。</returns>
    public bool TryGetHwTexture(AVFrame* frame, out IntPtr texturePtr, out int subResourceIndex, out ColorInfo color)
    {
        texturePtr = IntPtr.Zero;
        subResourceIndex = 0;
        color = ColorInfo.Default;

        var fmt = (AVPixelFormat)frame->format;
        if (fmt != AVPixelFormat.D3d11) return false;

        // D3D11VA では data[0] が ID3D11Texture2D 配列、data[1] が配列内の ArraySlice 番号を表す。
        texturePtr = (IntPtr)frame->data[0];
        subResourceIndex = (int)frame->data[1];
        if (texturePtr == IntPtr.Zero) return false;

        color = ResolveColorInfo(frame);
        return true;
    }

    /// <summary>フレームのメタデータから色変換マトリクス・レンジを推定する。不明時は <see cref="ColorInfo.Default"/>（BT.709/Limited）。</summary>
    private static ColorInfo ResolveColorInfo(AVFrame* frame)
    {
        // BT.601 系（SD 由来）だけ明示的に false、それ以外（BT.709・未指定）は既定の BT.709 とみなす。
        var cs = frame->colorspace;
        bool isBt709 = !(cs == AVColorSpace.Bt470bg || cs == AVColorSpace.Smpte170m || cs == AVColorSpace.Fcc);

        // color_range: JPEG=フルレンジ(0-255)、それ以外(MPEG/未指定)=リミテッド(16-235)。
        bool isFullRange = frame->color_range == AVColorRange.Jpeg;

        return new ColorInfo(isBt709, isFullRange);
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
