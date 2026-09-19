using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 統合テスト用のメディアファイルを、その場で組み立てる。
/// </summary>
/// <remarks>
/// <b>リポジトリにバイナリを置かない方針。</b> 収録済みのファイルを持つと、尺・トラック数・
/// コーデックを変えたくなるたびに差し替えが要り、何を試しているファイルなのかコードから読めなくなる。
/// ここで作れば「3 秒・音声 2 トラック」といった条件がテストの中に書ける。
/// <para>
/// <b>エンコーダは同梱の FFmpeg にあるものだけを使う</b>（<c>libx264</c> / <c>aac</c>。
/// <c>ffmpeg</c> コマンドは PATH に無い前提）。生成に <c>Sdcb.FFmpeg</c> のラッパー API を
/// 使っているのは、ここがテストの足場で短さと読みやすさを優先するため——本番側（<c>Decoding/</c>）は
/// 生の API を直接使っており、そちらの流儀を変えるものではない。
/// </para>
/// </remarks>
internal static unsafe class TestMediaFactory
{
    private const int DefaultWidth = 320;
    private const int DefaultHeight = 240;
    private const int DefaultFps = 25;

    /// <summary>
    /// 既定のキーフレーム間隔（フレーム数）。<b>フレームレートと同じ＝1 秒ごと</b>。
    /// </summary>
    /// <remarks>
    /// 公開しているのは、呼び出し側が「既定のまま」と「明示的に指定する」を
    /// <b>同じ値の書き写しなしに</b>切り替えられるようにするため。
    /// </remarks>
    internal const int DefaultGopSize = DefaultFps;

    /// <summary>
    /// 音声サンプルレート。<b>本番のミキサー軸（48kHz）と揃えてある</b>ので、
    /// リサンプラの都合がテストの期待値に混ざらない。
    /// </summary>
    private const int SampleRate = 48_000;

    /// <summary>
    /// トラックごとに違う高さの音を入れる。<b>どのトラックが鳴っているかを波形から見分けられる</b>
    /// ようにするため（ミキサーのトラック個別音量・ミュートの検証で要る）。
    /// </summary>
    private const double BaseFrequency = 220.0;

    /// <summary>
    /// 映像・音声を含む mp4 を作る。
    /// </summary>
    /// <param name="path">出力先。拡張子から muxer が決まる。</param>
    /// <param name="duration">おおよその尺。フレーム境界で切り上がる。</param>
    /// <param name="audioTrackCount">音声トラック数。1 以上。</param>
    /// <param name="gopSize">
    /// キーフレーム間隔（フレーム数）。<b>シークの粒度を決める</b>ので、シークの試験では
    /// 小さめにしておくと着地点が読みやすい。
    /// </param>
    /// <param name="includeVideo">
    /// 映像ストリームを含めるか。<c>false</c> にすると音声だけのファイルになる
    /// （音声のみのファイルは実際の利用形態のひとつで、映像側の経路を通らない）。
    /// </param>
    /// <param name="videoDuration">
    /// 映像だけを別の尺にする場合に指定する。<c>null</c> なら <paramref name="duration"/> と同じ。
    /// <b>映像が音声より先に終わるファイルを作るためにある</b>——映像の滞留検出は「提示が
    /// 来ないこと」を見るので、<b>再生を続けたまま映像だけ枯らす</b>必要がある
    /// （プルを止めると <c>CanObserveVideoStall</c> が観測自体をやめるため、そちらでは作れない）。
    /// </param>
    public static void CreateMp4(
        string path,
        TimeSpan duration,
        int audioTrackCount = 1,
        int gopSize = DefaultGopSize,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int fps = DefaultFps,
        bool includeVideo = true,
        TimeSpan? videoDuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(audioTrackCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration.TotalSeconds, 0);
        if (videoDuration is { } vd)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(vd.TotalSeconds, 0);

        // FFmpeg の言い分を添えて投げ直す。ラッパーの例外は「[FFmpeg error -22]: Invalid argument」
        // のように理由を落とすため、これが無いと生成の失敗を追う手掛かりが 1 つも残らない
        var messages = new List<string>();
        BeginCollecting(messages);
        try
        {
            CreateCore(path, duration, audioTrackCount, gopSize, width, height, fps,
                includeVideo, videoDuration ?? duration);
        }
        catch (Exception ex)
        {
            string detail;
            lock (LogGate)
            {
                detail = messages.Count > 0
                    ? string.Join(Environment.NewLine, messages)
                    : "（FFmpeg からの出力なし）";
            }
            throw new InvalidOperationException(
                $"テスト用メディアの生成に失敗した path={path}{Environment.NewLine}{detail}", ex);
        }
        finally
        {
            EndCollecting(messages);
        }
    }

    private static readonly object LogGate = new();

    /// <summary>
    /// いま収集中の入れ物。<b>複数ある</b>のは、テストクラスが並列に走って同時に生成しうるため。
    /// </summary>
    /// <remarks>
    /// <b>同時に生成している別クラスのメッセージが混ざりうる。</b> FFmpeg のログは
    /// プロセス全体で 1 本なので、どの生成に由来するかを区別する手立てが無い。混ざるのは
    /// 失敗時の説明が少し騒がしくなるだけで、取りこぼすより良いと判断した。
    /// </remarks>
    private static readonly List<List<string>> ActiveLogs = [];

    static TestMediaFactory()
    {
        // **FFmpeg のログ設定はプロセス全体で 1 つ。** 書き手には getter が無く元の値を控えられない
        // ため、ここで 1 度だけ差し替えて以後は収集側だけを入れ替える（掛け外しを繰り返さない）
        FFmpegLogger.LogLevel = LogLevel.Warning;
        FFmpegLogger.LogWriter = (level, message) =>
        {
            lock (LogGate)
            {
                if (ActiveLogs.Count == 0) return;
                string line = $"[{level}] {message?.TrimEnd()}";
                foreach (List<string> sink in ActiveLogs) sink.Add(line);
            }
        };
    }

    private static void BeginCollecting(List<string> sink)
    {
        lock (LogGate) ActiveLogs.Add(sink);
    }

    private static void EndCollecting(List<string> sink)
    {
        lock (LogGate) ActiveLogs.Remove(sink);
    }

    private static void CreateCore(
        string path, TimeSpan duration, int audioTrackCount, int gopSize,
        int width, int height, int fps, bool includeVideo, TimeSpan videoDuration)
    {
        Codec? videoCodec = includeVideo
            ? Codec.FindEncoderByName("libx264")
                ?? throw new InvalidOperationException("libx264 エンコーダが同梱の FFmpeg に無い")
            : null;
        Codec audioCodec = Codec.FindEncoderByName("aac")
            ?? throw new InvalidOperationException("aac エンコーダが同梱の FFmpeg に無い");

        int videoFrameCount = (int)Math.Ceiling(videoDuration.TotalSeconds * fps);

        using FormatContext fc = FormatContext.AllocOutput(fileName: path);
        // mp4 は extradata をファイル先頭のヘッダへ置く。この指定が無いと各パケットに
        // 埋め込む形になり、デコーダがストリームの形を掴めないファイルができる
        bool globalHeader = (fc.OutputFormat!.Value.Flags & AVFMT.Globalheader) != 0;

        var disposables = new List<IDisposable>();
        try
        {
            (MediaStream stream, CodecContext ctx)? video = null;
            if (videoCodec != null)
            {
                video = AddVideoStream(
                    fc, videoCodec.Value, width, height, fps, gopSize, globalHeader);
                disposables.Add(video.Value.ctx);
            }

            var audio = new List<(MediaStream stream, CodecContext ctx)>();
            for (int i = 0; i < audioTrackCount; i++)
            {
                var track = AddAudioStream(fc, audioCodec, globalHeader);
                disposables.Add(track.ctx);
                audio.Add(track);
            }

            using IOContext io = IOContext.OpenWrite(path);
            fc.Pb = io;
            fc.WriteHeader();

            using var packet = new Packet();

            // **映像を先に全部、続いてトラックごとに全部書く。** InterleavedWritePacket が
            // ストリーム間の順序を整えるので、これでも正しいファイルになる。尺が数秒なので
            // 整列のための保持量も問題にならない（時刻順に交互へ組み替える価値が無い）
            if (video != null)
                EncodeVideo(fc, video.Value.ctx, video.Value.stream, videoFrameCount, packet);
            for (int i = 0; i < audio.Count; i++)
            {
                (MediaStream stream, CodecContext ctx) = audio[i];
                EncodeAudioTrack(fc, ctx, stream, duration, i, packet);
            }

            fc.WriteTrailer();
        }
        finally
        {
            foreach (IDisposable d in disposables) d.Dispose();
        }
    }

    private static (MediaStream stream, CodecContext ctx) AddVideoStream(
        FormatContext fc, Codec codec, int width, int height, int fps, int gopSize, bool globalHeader)
    {
        MediaStream stream = fc.NewStream(codec);
        var ctx = new CodecContext(codec)
        {
            Width = width,
            Height = height,
            PixelFormat = AVPixelFormat.Yuv420p,
            TimeBase = new AVRational(1, fps),
            Framerate = new AVRational(fps, 1),
            BitRate = 200_000,
            GopSize = gopSize,
            // **B フレームを使わない。** pts と dts が一致するので、生成側の時刻計算が
            // そのままファイル上の時刻になり、シークの期待値を立てやすい
            MaxBFrames = 0,
        };
        if (globalHeader) ctx.Flags |= AV_CODEC_FLAG.GlobalHeader;

        using var options = new MediaDictionary();
        // テストのたびに走るので速さを優先する。画質は判定に使わない
        options.Set("preset", "ultrafast");
        options.Set("tune", "zerolatency");
        ctx.Open(codec, options);
        PublishToStream(stream, ctx);
        return (stream, ctx);
    }

    private static (MediaStream stream, CodecContext ctx) AddAudioStream(
        FormatContext fc, Codec codec, bool globalHeader)
    {
        MediaStream stream = fc.NewStream(codec);
        var ctx = new CodecContext(codec)
        {
            SampleFormat = codec.NegociateSampleFormat(AVSampleFormat.Fltp),
            SampleRate = SampleRate,
            ChLayout = StereoLayout(),
            BitRate = 96_000,
            TimeBase = new AVRational(1, SampleRate),
        };
        if (globalHeader) ctx.Flags |= AV_CODEC_FLAG.GlobalHeader;

        ctx.Open(codec);
        PublishToStream(stream, ctx);
        return (stream, ctx);
    }

    /// <summary>
    /// エンコーダの設定をストリームへ写す（コーデック情報と時間軸）。
    /// </summary>
    /// <remarks>
    /// <b>ラッパーの <c>CodecContext.FillParameters(stream.Codecpar)</c> を使わないこと。</b>
    /// あれを通すとストリーム側の <c>codecpar</c> に書き込まれず、
    /// <c>avformat_write_header</c> が
    /// 「Could not find tag for codec none in stream #0」で落ちる（実測）。
    /// ここは生の API で <c>AVStream</c> のメンバーへ直接書く。
    /// </remarks>
    private static void PublishToStream(MediaStream stream, CodecContext ctx)
    {
        AVStream* raw = stream;
        int ret = ffmpeg.avcodec_parameters_from_context(raw->codecpar, ctx);
        if (ret < 0)
        {
            throw new InvalidOperationException(
                $"avcodec_parameters_from_context が失敗した ret={ret}");
        }
        raw->time_base = ctx.TimeBase;
    }

    private static AVChannelLayout StereoLayout()
    {
        AVChannelLayout layout = default;
        ffmpeg.av_channel_layout_default(&layout, 2);
        return layout;
    }

    /// <summary>
    /// 映像をエンコードして書き出す。絵は 1 フレームごとに明るさが変わるだけの単純な模様。
    /// </summary>
    /// <remarks>
    /// <b>フレームは自分で作って自分で破棄する。</b> ライブラリの
    /// <c>VideoFrameGenerator</c> は <c>IEnumerable&lt;Frame&gt;</c> を返すが、
    /// <b>受け取った側が破棄すべきか・使い回されているのかが呼び出し側から判別できない</b>。
    /// 破棄しないままにするとファイナライザが GC スレッドでネイティブ解放を走らせることになり、
    /// 実際に<b>テストの実行後にホストプロセスが落ちた</b>（3 回に 2 回。落ちる場所はテストの
    /// 外なので、どのテストが原因かも分からない形で出る）。所有を曖昧にしないこと。
    /// <para>
    /// 絵の内容は判定に使わない。<b>フレームごとに違う絵にしてある</b>のは、全フレーム同一だと
    /// エンコーダがほぼ空のパケットにまとめてしまい、シークの着地点を試すだけの尺が
    /// 得られないため。
    /// </para>
    /// </remarks>
    private static void EncodeVideo(
        FormatContext fc, CodecContext ctx, MediaStream stream, int frameCount, Packet packet)
    {
        for (int i = 0; i < frameCount; i++)
        {
            using Frame frame = Frame.CreateVideo(ctx.Width, ctx.Height, ctx.PixelFormat);
            AllocateFrameBuffer(frame);
            FillGradient(frame, ctx.Width, ctx.Height, i);
            frame.Pts = i;
            WriteEncoded(fc, ctx, stream, frame, packet);
        }

        // エンコーダに溜まった分を出し切る
        WriteEncoded(fc, ctx, stream, null, packet);
    }

    /// <summary>YUV420p のフレームへ、フレーム番号で変化する模様を書き込む。</summary>
    private static void FillGradient(Frame frame, int width, int height, int frameIndex)
    {
        AVFrame* raw = frame;

        var y = (byte*)raw->data[0];
        int yStride = raw->linesize[0];
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                y[row * yStride + col] = (byte)((col + row + frameIndex * 4) & 0xFF);

        // 色差は中間値で埋める（無彩色）。縦横ともに半分の解像度
        for (int plane = 1; plane <= 2; plane++)
        {
            var c = (byte*)raw->data[plane];
            int stride = raw->linesize[plane];
            for (int row = 0; row < height / 2; row++)
                for (int col = 0; col < width / 2; col++)
                    c[row * stride + col] = 128;
        }
    }

    /// <summary>
    /// 1 トラック分の音声をエンコードして書き出す。
    /// </summary>
    /// <remarks>
    /// <b>フレームを毎回作り直している。</b> 1 つを使い回して <c>MakeWritable</c> で書き込み可能に
    /// 戻す形が本来の作法だが、ラッパーの <c>Frame.MakeWritable()</c> はバッファを確保した後でも
    /// EINVAL を返した（実測）。<b>使い回しはエンコーダがフレームを参照し続けた場合の
    /// 所有権の問題も抱える</b>ため、確保し直す方を採った——数秒のファイルで数百個なので、
    /// 速さは判定に影響しない。
    /// </remarks>
    private static void EncodeAudioTrack(
        FormatContext fc, CodecContext ctx, MediaStream stream,
        TimeSpan duration, int trackIndex, Packet packet)
    {
        // エンコーダが受け取るサンプル数は固定（AAC なら 1024）。Open した後でしか分からない
        int frameSize = ctx.FrameSize > 0 ? ctx.FrameSize : 1024;
        long totalSamples = (long)(duration.TotalSeconds * SampleRate);
        double frequency = BaseFrequency * (trackIndex + 1);
        int channels = ctx.ChLayout.nb_channels;

        if (ffmpeg.av_sample_fmt_is_planar(ctx.SampleFormat) == 0)
        {
            throw new NotSupportedException(
                $"インターリーブ形式のサンプル形式には未対応: {ctx.SampleFormat}");
        }

        for (long written = 0; written < totalSamples; written += frameSize)
        {
            using Frame frame =
                Frame.CreateAudio(ctx.SampleFormat, ctx.ChLayout, SampleRate, frameSize);
            AllocateFrameBuffer(frame);
            FillSine(frame, channels, frameSize, frequency, written);
            frame.Pts = written;
            WriteEncoded(fc, ctx, stream, frame, packet);
        }

        // エンコーダに溜まった分を出し切る
        WriteEncoded(fc, ctx, stream, null, packet);
    }

    /// <summary>
    /// フレームに画素・サンプル用のバッファを確保する。既に確保されていれば何もしない。
    /// </summary>
    /// <remarks>
    /// <b>この版の <c>Frame.CreateAudio</c> / <c>CreateVideo</c> は確保まで行う</b>ため、実際には
    /// 下の早期 return を通る（確保しない版なら <c>av_frame_get_buffer</c> の側が働く）。
    /// 確保済みかを見ずに呼ぶと、既にあるバッファを取りこぼすことになるので順序を変えないこと。
    /// </remarks>
    private static void AllocateFrameBuffer(Frame frame)
    {
        AVFrame* raw = frame;
        if (raw->buf[0] != IntPtr.Zero) return;

        int ret = ffmpeg.av_frame_get_buffer(raw, 0);
        if (ret < 0)
            throw new InvalidOperationException($"av_frame_get_buffer が失敗した ret={ret}");
    }

    /// <summary>プレーナ形式のフレームへ正弦波を書き込む。</summary>
    private static void FillSine(
        Frame frame, int channels, int sampleCount, double frequency, long startSample)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            var dst = (float*)frame.Data[ch];
            for (int i = 0; i < sampleCount; i++)
            {
                double t = (startSample + i) / (double)SampleRate;
                dst[i] = (float)(Math.Sin(2 * Math.PI * frequency * t) * 0.25);
            }
        }
    }

    /// <summary>
    /// 1 フレームをエンコードし、出てきたパケットをすべて書き出す。
    /// </summary>
    /// <remarks>
    /// <paramref name="frame"/> に <c>null</c> を渡すとエンコーダの終端を伝える。
    /// <b>この呼び出しを省かないこと。</b> エンコーダは内部にフレームを溜めるため、
    /// 流し込むだけでは末尾が欠けたファイルになる。
    /// </remarks>
    private static void WriteEncoded(
        FormatContext fc, CodecContext ctx, MediaStream stream, Frame? frame, Packet packet)
    {
        foreach (Packet encoded in ctx.EncodeFrame(frame!, packet))
        {
            encoded.StreamIndex = stream.Index;
            encoded.RescaleTimestamp(ctx.TimeBase, stream.TimeBase);
            fc.InterleavedWritePacket(encoded);
        }
    }
}
