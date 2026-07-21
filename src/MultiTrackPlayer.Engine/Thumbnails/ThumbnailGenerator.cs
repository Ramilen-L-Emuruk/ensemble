using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiTrackPlayer.Core.Models;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Thumbnails;

/// <summary>
/// シークバーホバー用サムネイルの事前生成。本編再生パイプライン（DemuxThread が専有する
/// AVFormatContext）とは完全に独立した AVFormatContext を開き、ソフトウェアデコードのみで
/// 低解像度スプライトシートを1枚のJPEGへ焼き込む。GPUデコードは使わない（本編との競合を避けるため）。
/// </summary>
public static unsafe class ThumbnailGenerator
{
    // MJPEGのqscale値。小さいほど高品質・大きいほど低品質。プレビュー用途として5は十分な画質
    private const int JpegQp = 5;
    // この間隔ごとに、途中経過のJPEGを書き出して onProgress を呼ぶ。長尺ファイルでも
    // 生成済みの先頭部分から順にホバー表示できるようにするため
    private static readonly TimeSpan ProgressFlushInterval = TimeSpan.FromSeconds(1.5);
    // 粗いプレビューパスの最終的なプローブ数（最も細かい段階での分割数）。2→4→8→16と段階的に
    // 密度を上げていき、各段階の終わりで進捗を通知する。前の段階で既にデコード済みの地点は
    // 再利用するため、合計のデコード数は最終段階の16回のまま増えない
    private const int CoarsePassMaxProbes = 16;
    // 1回のシーク後、目標PTS以降のキーフレームを探すために読むパケット数の上限（壊れたファイルでの無限ループ防止）。
    // 音声トラックが複数本多重化されているファイルでは、映像パケットは全体のごく一部に薄まる
    // （実測: 5音声トラック混在時、1GOP分の映像に届く前に300パケットで枯渇していた）ため、
    // 複数GOP分をカバーできるよう十分大きめに取る
    private const int SeekGuardPackets = 3000;

    public static ThumbnailSheet? Generate(
        string filePath, string outputJpgPath,
        double durationSeconds, int mediaWidth, int mediaHeight,
        int targetTileWidth, CancellationToken ct,
        Action<ThumbnailSheet>? onProgress = null)
    {
        if (durationSeconds <= 0 || mediaWidth <= 0 || mediaHeight <= 0) return null;

        var sampleTimes = ThumbnailPlan.ComputeSampleTimes(durationSeconds);
        if (sampleTimes.Count == 0) return null;

        int tileWidth = Math.Max(2, targetTileWidth - targetTileWidth % 2);
        int tileHeight = (int)Math.Round(tileWidth * (double)mediaHeight / mediaWidth);
        tileHeight = Math.Max(2, tileHeight - tileHeight % 2);

        int columns = ThumbnailPlan.ComputeColumns(sampleTimes.Count);
        int rows = (int)Math.Ceiling(sampleTimes.Count / (double)columns);

        AVFormatContext* fmtCtx = null;
        AVCodecContext* decCtx = null;
        AVCodecContext* encCtx = null;
        SwsContext* swsCtx = null;
        AVFrame* decFrame = null;
        AVFrame* sheetFrame = null;
        AVFrame* tileFrame = null;
        AVPacket* pkt = null;

        try
        {
            if (avformat_open_input(&fmtCtx, filePath, null, null) < 0) return null;
            if (avformat_find_stream_info(fmtCtx, null) < 0) return null;

            int videoStreamIndex = -1;
            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.Video)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
            if (videoStreamIndex < 0) return null;

            var stream = fmtCtx->streams[videoStreamIndex];
            var timeBase = stream->time_base;
            var codec = avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null) return null;

            decCtx = avcodec_alloc_context3(codec);
            if (decCtx == null) return null;
            avcodec_parameters_to_context(decCtx, stream->codecpar);
            // デフォルト（thread_count=0=自動）だとHEVC等でフレーム並列デコードが有効になり、
            // 内部にパイプライン遅延を持つ（孤立したキーフレームを1枚送っただけでは
            // avcodec_receive_frame がすぐには成功しない）。マルチスレッド化すれば本編再生との
            // CPU競合時に有利になるかと試したが、実測で改善は見られなかった（本編側が既に
            // コアを使い切っているため）ため、無駄な複雑さを避けてシングルスレッドに固定する
            decCtx->thread_count = 1;
            if (avcodec_open2(decCtx, codec, null) < 0) return null;

            int sheetWidth = columns * tileWidth;
            int sheetHeight = rows * tileHeight;

            sheetFrame = av_frame_alloc();
            sheetFrame->format = (int)AVPixelFormat.Yuvj420p;
            sheetFrame->width = sheetWidth;
            sheetFrame->height = sheetHeight;
            if (av_frame_get_buffer(sheetFrame, 32) < 0) return null;
            FillGray(sheetFrame);

            // JPEGエンコーダは1回だけ開いて使い回す。呼び出しのたびに開き直すと
            // （進捗表示のための途中経過エンコードを含めて）想定以上にコストがかかり、
            // 進捗表示の間隔を詰めるほど全体が遅くなる逆効果が実測で確認された
            encCtx = OpenJpegEncoder(sheetWidth, sheetHeight);
            if (encCtx == null) return null;

            // 1つのキーフレームが複数のサンプル枠を埋めることがあるため、縮小は1回だけ行い
            // タイルバッファへコピー（ブリット）して使い回す。サンプル枠ごとに sws_scale を
            // やり直すのは無駄が大きい
            tileFrame = av_frame_alloc();
            tileFrame->format = (int)AVPixelFormat.Yuvj420p;
            tileFrame->width = tileWidth;
            tileFrame->height = tileHeight;
            if (av_frame_get_buffer(tileFrame, 32) < 0) return null;

            decFrame = av_frame_alloc();
            pkt = av_packet_alloc();

            double interval = ThumbnailPlan.ComputeInterval(durationSeconds);
            int version = 0;
            var progressTimer = Stopwatch.StartNew();

            // 粗いプレビューパス: 動画全体に均等にプローブを打ち、区間ごとに近いキーフレームで
            // ざっくり埋める。動画の後半・終盤にホバーしても、精密パス（本編を先頭から順に読む
            // パス）が追いつくまで真っ灰色のまま待たされる問題を避けるため、最初に全区間へ
            // 「一応何か見える」状態を作ってから、精密パスで上書きしていく。
            // 密度は2→4→8→…と段階的に上げ、各段階の終わりで進捗を通知する。前の段階で
            // 既にデコード済みの地点（例: 4分割の0番目と2番目は2分割の0番目・1番目と同じ時刻）は
            // 再デコードせず使い回すため、最終段階までの合計デコード数は一度に16点打つのと変わらない
            int finestCount = Math.Min(CoarsePassMaxProbes, sampleTimes.Count);
            if (finestCount > 0)
            {
                var stageCounts = new List<int>();
                for (int n = 2; n < finestCount; n *= 2)
                    stageCounts.Add(n);
                stageCounts.Add(finestCount);

                var tileCache = new byte[finestCount][];
                byte[]? lastGoodTile = null;

                foreach (int stageN in stageCounts)
                {
                    if (ct.IsCancellationRequested) break;

                    int step = finestCount / stageN;
                    double probeSpacing = durationSeconds / stageN;
                    for (int i = 0; i < stageN && !ct.IsCancellationRequested; i++)
                    {
                        int slot = i * step;
                        if (tileCache[slot] == null)
                        {
                            double probeTime = i * probeSpacing;
                            if (TrySeekAndDecodeKeyframeAt(fmtCtx, decCtx, pkt, decFrame, videoStreamIndex, timeBase, probeTime))
                            {
                                ScaleToTile(decFrame, tileFrame, ref swsCtx);
                                tileCache[slot] = CaptureTile(tileFrame, tileWidth, tileHeight);
                            }
                        }

                        // このプローブが失敗しても、直前に成功したプローブの絵を最寄りの代用として
                        // 流用する。何も無ければ（先頭プローブが失敗した場合のみ）グレーのままにする
                        byte[]? tile = tileCache[slot] ?? lastGoodTile;
                        if (tile == null) continue;
                        lastGoodTile = tile;

                        double probeTime2 = i * probeSpacing;
                        double segmentEnd = (i == stageN - 1) ? durationSeconds : probeTime2 + probeSpacing;
                        int startIdx = Math.Max(0, (int)Math.Ceiling(probeTime2 / interval - 1e-6));
                        int endIdx = Math.Min(sampleTimes.Count, (int)Math.Ceiling(segmentEnd / interval - 1e-6));
                        for (int idx = startIdx; idx < endIdx; idx++)
                            BlitTileFromBuffer(tile, sheetFrame, idx, columns, tileWidth, tileHeight);
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        version++;
                        EncodeJpeg(encCtx, sheetFrame, outputJpgPath, version);
                        onProgress?.Invoke(new ThumbnailSheet(
                            outputJpgPath, columns, rows, tileWidth, tileHeight,
                            sampleTimes.Count, interval, durationSeconds, version) { IsComplete = false });
                        progressTimer.Restart();
                    }
                }

                // 精密パスのために先頭へシークし直す
                avformat_seek_file(fmtCtx, -1, long.MinValue, 0, 0, (int)AVSEEK_FLAG.Backward);
                avcodec_flush_buffers(decCtx);
            }

            // 精密パス: 先頭から1回だけ順方向にデコードし、通過したサンプル時刻の分だけタイルを埋める。
            // サンプルごとにシークし直す方式だと、GOP長（キーフレーム間隔）がサンプル間隔より
            // 長い動画（OBS録画など）で同じ区間を何度も重複デコードすることになり致命的に遅くなる
            // （実測で数分経っても終わらないケースがあった）ため、通し読み1パスに統一している
            int sampleIndex = 0;
            while (sampleIndex < sampleTimes.Count && !ct.IsCancellationRequested)
            {
                if (av_read_frame(fmtCtx, pkt) < 0) break; // ファイル終端

                if (pkt->stream_index != videoStreamIndex) { av_packet_unref(pkt); continue; }
                // キーフレーム以外は送らない。フレーム間予測を全部解くと5分程度のOBS録画でも
                // 数分単位で終わらないことがあったため、自己完結しているキーフレームだけを拾う。
                // タイルの粒度がキーフレーム間隔（数秒程度）に粗くなるが、ホバープレビュー用途では
                // 実用上問題にならない
                if ((pkt->flags & AV_PKT_FLAG_KEY) == 0) { av_packet_unref(pkt); continue; }

                int sendRet = avcodec_send_packet(decCtx, pkt);
                av_packet_unref(pkt);
                if (sendRet < 0) continue;

                while (sampleIndex < sampleTimes.Count && avcodec_receive_frame(decCtx, decFrame) == 0)
                {
                    long pts = decFrame->best_effort_timestamp;
                    double ptsSec = pts == long.MinValue ? 0.0 : pts * av_q2d(timeBase);
                    if (ptsSec + 1e-3 < sampleTimes[sampleIndex]) continue;

                    ScaleToTile(decFrame, tileFrame, ref swsCtx);
                    while (sampleIndex < sampleTimes.Count && ptsSec + 1e-3 >= sampleTimes[sampleIndex])
                    {
                        BlitTile(tileFrame, sheetFrame, sampleIndex, columns, tileWidth, tileHeight);
                        sampleIndex++;
                    }
                }

                // 生成済みの先頭部分から順にホバー表示できるよう、一定間隔でJPEGを途中経過として
                // 書き出す。長尺ファイルで全体が終わるまで何も表示されないのを避けるため
                if (onProgress != null && progressTimer.Elapsed >= ProgressFlushInterval && !ct.IsCancellationRequested)
                {
                    version++;
                    EncodeJpeg(encCtx, sheetFrame, outputJpgPath, version);
                    onProgress(new ThumbnailSheet(
                        outputJpgPath, columns, rows, tileWidth, tileHeight,
                        sampleTimes.Count, interval, durationSeconds, version) { IsComplete = false });
                    progressTimer.Restart();
                }
            }

            if (ct.IsCancellationRequested) return null;

            EncodeJpeg(encCtx, sheetFrame, outputJpgPath, version + 1);

            return new ThumbnailSheet(
                outputJpgPath, columns, rows, tileWidth, tileHeight,
                sampleTimes.Count, interval, durationSeconds, version + 1) { IsComplete = true };
        }
        finally
        {
            if (pkt != null) { AVPacket* pp = pkt; av_packet_free(&pp); }
            if (decFrame != null) { AVFrame* f = decFrame; av_frame_free(&f); }
            if (tileFrame != null) { AVFrame* f = tileFrame; av_frame_free(&f); }
            if (sheetFrame != null) { AVFrame* f = sheetFrame; av_frame_free(&f); }
            if (swsCtx != null) sws_freeContext(swsCtx);
            if (encCtx != null) { AVCodecContext* c = encCtx; avcodec_free_context(&c); }
            if (decCtx != null) { AVCodecContext* c = decCtx; avcodec_free_context(&c); }
            if (fmtCtx != null) { AVFormatContext* f = fmtCtx; avformat_close_input(&f); }
        }
    }

    /// <summary>
    /// 指定時刻付近へシークし、その直後にあるキーフレームをデコードする（粗いプレビューパス専用）。
    /// 精密パスのような通し読みではなく、少数のプローブ点だけに使うので許容できるコストで済む。
    /// </summary>
    private static bool TrySeekAndDecodeKeyframeAt(
        AVFormatContext* fmtCtx, AVCodecContext* decCtx, AVPacket* pkt, AVFrame* frame,
        int videoStreamIndex, AVRational timeBase, double targetSeconds)
    {
        long ts = (long)(targetSeconds * AV_TIME_BASE);
        avformat_seek_file(fmtCtx, -1, long.MinValue, ts, ts, (int)AVSEEK_FLAG.Backward);
        avcodec_flush_buffers(decCtx);

        int guard = 0;
        while (guard < SeekGuardPackets && av_read_frame(fmtCtx, pkt) >= 0)
        {
            guard++;
            if (pkt->stream_index != videoStreamIndex) { av_packet_unref(pkt); continue; }
            if ((pkt->flags & AV_PKT_FLAG_KEY) == 0) { av_packet_unref(pkt); continue; }

            int sendRet = avcodec_send_packet(decCtx, pkt);
            av_packet_unref(pkt);
            if (sendRet < 0) continue;

            // 孤立したキーフレーム1枚だけを送っても、デコーダ内部の並べ替え遅延により
            // avcodec_receive_frame がすぐには成功しないことがある（実測で確認済み: send成功でも
            // receiveが一度も成功せずガード上限まで空振りし続けていた）。「入力終了」を明示的に
            // 伝えて、溜め込んでいるフレームを吐き出させる
            avcodec_send_packet(decCtx, null);

            while (avcodec_receive_frame(decCtx, frame) == 0)
            {
                long pts = frame->best_effort_timestamp;
                double ptsSec = pts == long.MinValue ? 0.0 : pts * av_q2d(timeBase);
                if (ptsSec + 1e-3 >= targetSeconds) return true;
            }

            // 単発デコード用にEOFを送ったデコーダは、そのままでは以後の送信を受け付けなくなるため、
            // 次のプローブでも使い続けられるよう内部状態をリセットしておく
            avcodec_flush_buffers(decCtx);
        }
        return false;
    }

    /// <summary>デコード済みフレームを1回だけ縮小し、タイル用バッファに書き込む。</summary>
    private static void ScaleToTile(AVFrame* srcFrame, AVFrame* tileFrame, ref SwsContext* swsCtx)
    {
        int srcW = srcFrame->width;
        int srcH = srcFrame->height;
        if (srcW <= 0 || srcH <= 0) return;

        swsCtx = sws_getCachedContext(
            swsCtx, srcW, srcH, (AVPixelFormat)srcFrame->format,
            tileFrame->width, tileFrame->height, AVPixelFormat.Yuvj420p,
            2, null, null, null); // 2 = SWS_BILINEAR
        if (swsCtx == null) return;

        var srcData = new byte*[] {
            (byte*)srcFrame->data[0], (byte*)srcFrame->data[1], (byte*)srcFrame->data[2], (byte*)srcFrame->data[3], null, null, null, null
        };
        var srcStride = new int[] {
            srcFrame->linesize[0], srcFrame->linesize[1], srcFrame->linesize[2], srcFrame->linesize[3], 0, 0, 0, 0
        };
        var dstData = new byte*[] {
            (byte*)tileFrame->data[0], (byte*)tileFrame->data[1], (byte*)tileFrame->data[2], null, null, null, null, null
        };
        var dstStride = new int[] {
            tileFrame->linesize[0], tileFrame->linesize[1], tileFrame->linesize[2], 0, 0, 0, 0, 0
        };

        sws_scale(swsCtx, srcData, srcStride, 0, srcH, dstData, dstStride);
    }

    /// <summary>縮小済みタイルバッファを、スプライトシートの指定枠へコピー（ブリット）する。</summary>
    private static void BlitTile(AVFrame* tileFrame, AVFrame* sheetFrame, int sampleIndex, int columns, int tileWidth, int tileHeight)
    {
        int col = sampleIndex % columns;
        int row = sampleIndex / columns;

        byte* yDst = (byte*)sheetFrame->data[0] + (long)row * tileHeight * sheetFrame->linesize[0] + col * tileWidth;
        byte* uDst = (byte*)sheetFrame->data[1] + (long)row * (tileHeight / 2) * sheetFrame->linesize[1] + col * (tileWidth / 2);
        byte* vDst = (byte*)sheetFrame->data[2] + (long)row * (tileHeight / 2) * sheetFrame->linesize[2] + col * (tileWidth / 2);

        byte* ySrc = (byte*)tileFrame->data[0];
        byte* uSrc = (byte*)tileFrame->data[1];
        byte* vSrc = (byte*)tileFrame->data[2];

        for (int r = 0; r < tileHeight; r++)
            new Span<byte>(ySrc + r * tileFrame->linesize[0], tileWidth)
                .CopyTo(new Span<byte>(yDst + r * sheetFrame->linesize[0], tileWidth));

        int chromaW = tileWidth / 2;
        int chromaH = tileHeight / 2;
        for (int r = 0; r < chromaH; r++)
        {
            new Span<byte>(uSrc + r * tileFrame->linesize[1], chromaW)
                .CopyTo(new Span<byte>(uDst + r * sheetFrame->linesize[1], chromaW));
            new Span<byte>(vSrc + r * tileFrame->linesize[2], chromaW)
                .CopyTo(new Span<byte>(vDst + r * sheetFrame->linesize[2], chromaW));
        }
    }

    /// <summary>
    /// タイルバッファの内容（Y/U/V）を、段階間で使い回せるようタイトに詰めたバイト配列へコピーする
    /// （粗いプレビューパスの段階的な密度上げ専用）。
    /// </summary>
    private static byte[] CaptureTile(AVFrame* tileFrame, int tileWidth, int tileHeight)
    {
        int chromaW = tileWidth / 2;
        int chromaH = tileHeight / 2;
        var buf = new byte[tileWidth * tileHeight + 2 * chromaW * chromaH];

        byte* ySrc = (byte*)tileFrame->data[0];
        int offset = 0;
        for (int r = 0; r < tileHeight; r++)
        {
            new Span<byte>(ySrc + r * tileFrame->linesize[0], tileWidth).CopyTo(buf.AsSpan(offset, tileWidth));
            offset += tileWidth;
        }

        byte* uSrc = (byte*)tileFrame->data[1];
        for (int r = 0; r < chromaH; r++)
        {
            new Span<byte>(uSrc + r * tileFrame->linesize[1], chromaW).CopyTo(buf.AsSpan(offset, chromaW));
            offset += chromaW;
        }

        byte* vSrc = (byte*)tileFrame->data[2];
        for (int r = 0; r < chromaH; r++)
        {
            new Span<byte>(vSrc + r * tileFrame->linesize[2], chromaW).CopyTo(buf.AsSpan(offset, chromaW));
            offset += chromaW;
        }

        return buf;
    }

    /// <summary>CaptureTile で詰めたバイト配列を、スプライトシートの指定枠へブリットする。</summary>
    private static void BlitTileFromBuffer(byte[] tile, AVFrame* sheetFrame, int sampleIndex, int columns, int tileWidth, int tileHeight)
    {
        int col = sampleIndex % columns;
        int row = sampleIndex / columns;
        int chromaW = tileWidth / 2;
        int chromaH = tileHeight / 2;

        byte* yDst = (byte*)sheetFrame->data[0] + (long)row * tileHeight * sheetFrame->linesize[0] + col * tileWidth;
        byte* uDst = (byte*)sheetFrame->data[1] + (long)row * chromaH * sheetFrame->linesize[1] + col * chromaW;
        byte* vDst = (byte*)sheetFrame->data[2] + (long)row * chromaH * sheetFrame->linesize[2] + col * chromaW;

        fixed (byte* bufPtr = tile)
        {
            byte* ySrc = bufPtr;
            byte* uSrc = ySrc + tileWidth * tileHeight;
            byte* vSrc = uSrc + chromaW * chromaH;

            for (int r = 0; r < tileHeight; r++)
                new Span<byte>(ySrc + r * tileWidth, tileWidth)
                    .CopyTo(new Span<byte>(yDst + r * sheetFrame->linesize[0], tileWidth));

            for (int r = 0; r < chromaH; r++)
            {
                new Span<byte>(uSrc + r * chromaW, chromaW)
                    .CopyTo(new Span<byte>(uDst + r * sheetFrame->linesize[1], chromaW));
                new Span<byte>(vSrc + r * chromaW, chromaW)
                    .CopyTo(new Span<byte>(vDst + r * sheetFrame->linesize[2], chromaW));
            }
        }
    }

    /// <summary>末尾の未生成タイル（総数がグリッドを割り切らない端数）を中間グレーで塗っておく。</summary>
    private static void FillGray(AVFrame* frame)
    {
        byte* y = (byte*)frame->data[0];
        for (int row = 0; row < frame->height; row++)
            new Span<byte>(y + row * frame->linesize[0], frame->width).Fill(128);

        int chromaW = frame->width / 2;
        int chromaH = frame->height / 2;
        byte* u = (byte*)frame->data[1];
        byte* v = (byte*)frame->data[2];
        for (int row = 0; row < chromaH; row++)
        {
            new Span<byte>(u + row * frame->linesize[1], chromaW).Fill(128);
            new Span<byte>(v + row * frame->linesize[2], chromaW).Fill(128);
        }
    }

    /// <summary>MJPEGエンコーダを1回だけ開く。呼び出しのたびに開き直すとコストが大きいため使い回す。</summary>
    private static AVCodecContext* OpenJpegEncoder(int width, int height)
    {
        var codec = avcodec_find_encoder(AVCodecID.Mjpeg);
        if (codec == null) return null;

        AVCodecContext* encCtx = avcodec_alloc_context3(codec);
        if (encCtx == null) return null;

        encCtx->width = width;
        encCtx->height = height;
        encCtx->pix_fmt = AVPixelFormat.Yuvj420p;
        encCtx->time_base = new AVRational { Num = 1, Den = 1 };
        encCtx->flags |= (int)AV_CODEC_FLAG.Qscale;
        encCtx->global_quality = FF_QP2LAMBDA * JpegQp;

        if (avcodec_open2(encCtx, codec, null) < 0)
        {
            avcodec_free_context(&encCtx);
            return null;
        }
        return encCtx;
    }

    private static void EncodeJpeg(AVCodecContext* encCtx, AVFrame* sheetFrame, string outputPath, long ptsValue)
    {
        AVPacket* outPkt = null;
        try
        {
            // 同じエンコーダを使い回して複数回エンコードするため、呼び出しごとに単調増加させる
            sheetFrame->pts = ptsValue;
            if (avcodec_send_frame(encCtx, sheetFrame) < 0)
                throw new InvalidOperationException("Could not send frame to MJPEG encoder");

            outPkt = av_packet_alloc();
            if (avcodec_receive_packet(encCtx, outPkt) < 0)
                throw new InvalidOperationException("Could not receive packet from MJPEG encoder");

            var bytes = new byte[outPkt->size];
            Marshal.Copy((IntPtr)outPkt->data, bytes, 0, outPkt->size);
            File.WriteAllBytes(outputPath, bytes);
        }
        finally
        {
            if (outPkt != null) { AVPacket* p = outPkt; av_packet_free(&p); }
        }
    }
}
