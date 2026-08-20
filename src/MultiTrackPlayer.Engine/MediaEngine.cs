using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Interfaces;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Pipeline;
using MultiTrackPlayer.Engine.Rendering;
using MultiTrackPlayer.Engine.Sync;
using MultiTrackPlayer.Engine.Utilities;
using MultiTrackPlayer.Engine.Video;
using NAudio.Wave;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CorePlaybackState = MultiTrackPlayer.Core.Enums.PlaybackState;

namespace MultiTrackPlayer.Engine;

public unsafe class MediaEngine : IMediaEngine
{
    private AVFormatContext* _fmtCtx;
    // 描画・デコードで共有する自前 D3D11 デバイス。初回 Open 時に一度だけ遅延生成し、ファイル切替では作り直さない。
    // GPU 無し環境等で生成に失敗した場合は null のままとし、VideoDecoder は従来の FFmpeg 自前生成経路へフォールバックする。
    private GpuDeviceContext? _gpuDevice;
    private bool _gpuDeviceInitAttempted;
    // FFmpeg の HW デバイスコンテキスト（D3D11VA）。共有 D3D11 デバイスと同様に初回 Open 時に一度だけ生成して使い回す。
    // ファイル切替のたびに av_hwdevice_ctx_init/uninit を繰り返すとネイティブヒープを破損させ連続 D&D でクラッシュしたため、
    // 1つを全 VideoDecoder が av_buffer_ref で参照共有する。GPU 無し環境等では null のままとしフォールバックする。
    private AVBufferRef* _sharedHwDeviceCtx;
    private VideoDecoder? _videoDecoder;
    private readonly List<AudioDecoder> _audioDecoders = new();
    private readonly List<AudioTrackState> _audioStates = new();
    private readonly Dictionary<int, int> _audioStreamToTrack = new();
    private MultiTrackMixer? _mixer;
    private WasapiOut? _wasapiOut;

    // ffplay 型パイプライン: demux/デコードは各専用スレッドが担当し、AVFormatContext は DemuxThread が唯一専有する
    private VideoPacketQueue? _videoQueue;
    private AudioPacketQueue? _audioQueue;
    // 映像フレームリング（読み出し側の共通契約）。HW デコード時は GPU ゼロコピー版、そうでなければ CPU 版。
    private IVideoFrameRing? _videoRing;
    // GPU 経路でのみ生成される色変換器（enumerator/processor を所有）。CPU 経路では null。
    private GpuFrameConverter? _videoConverter;
    private DemuxThread? _demuxThread;
    private VideoDecodeThread? _videoDecodeThread;
    private AudioDecodeThread? _audioDecodeThread;
    private Thread? _demuxThreadHandle;
    private Thread? _videoDecodeThreadHandle;
    private Thread? _audioDecodeThreadHandle;
    private Timer? _statusTimer;
    private volatile bool _playbackEndedFired;

    // 案Y: 映像を子ウィンドウのスワップチェーンへ vsync Present する vout（GPU デコード経路のときのみ稼働）。
    // 稼働中は UI の CompositionTarget.Rendering プルを使わず、専用スレッドが vsync（waitable）ごとに提示する。
    private IntPtr _videoOutputHwnd;
    private SwapChainVideoPresenter? _swapPresenter;
    private Thread? _voutThreadHandle;
    private volatile bool _voutRunning;
    private long _lastVoutPull;

    // Paused 中に表示するフレーム（Step/Seek で更新）。Playing 中は使わず TryLeaseDue を直接使う
    private VideoFrameLease? _heldLease;
    private bool _heldFrameConsumed = true;

    // audio-master クロック: mixer が書いたサンプル軸のセグメントマップ + WASAPI 実位置の写像
    private readonly PlaybackClock _clock = new(AudioDecoder.OutSampleRate);
    private IPlaybackPositionSource? _positionSource;
    private double _pendingAnchorTarget;
    private int _awaitingAnchor;

    // シーク後の音声・映像プリロール完了の両方を待つゲート（早送りバグの根治。詳細は Seek() 参照）
    private volatile bool _videoPrerollReady = true;
    private volatile bool _audioPrerollReady = true;

    private MediaInfo? _currentMedia;
    // SetState 以外からも素の読み取りが多数あるため volatile にする（書き込みは _stateLock で直列化）
    private volatile CorePlaybackState _state = CorePlaybackState.Stopped;
    private readonly object _stateLock = new();
    // 停止時に読み取り位置を巻き戻せなかった（スレッドが止まりきらなかった）ことを覚えておく。
    // このまま次の再生に入ると、実際の内容は停止位置からなのに表示だけ 0 秒から進んでしまう
    private bool _rewindSkipped;
    private double _playbackSpeed = 1.0;
    private List<ChapterInfo> _chapters = new();
    // 映像の1フレーム時間（秒）: due 判定・プリロール猶予・フレームドロップ閾値に使用
    private double _videoFrameDuration = 1.0 / 30.0;
    // フレームドロップ統計（100フレームごとに StatisticsUpdated イベントで通知）
    private int _droppedFrames;
    private int _displayedFrames;

    public MediaInfo? CurrentMedia => _currentMedia;
    /// <summary>現在の再生状態。UI 側はこの値を唯一の情報源として表示すること。</summary>
    /// <remarks>読み取りは volatile で足りる。書き込みだけ <c>_stateLock</c> で直列化している。</remarks>
    public CorePlaybackState State => _state;
    public double PlaybackSpeed => _playbackSpeed;

    public TimeSpan Position
    {
        get
        {
            if (_wasapiOut == null) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(GetMasterClockSeconds());
        }
    }

    private double GetMasterClockSeconds()
        => _positionSource == null ? 0.0 : _clock.PositionAt(_positionSource.GetPositionFrames());

    /// <summary>
    /// 再生状態が変化したときに発火する。UI スレッド以外（再生完了検出は状態タイマー＝
    /// スレッドプール）からも発火するため、購読側でディスパッチャへ移すこと。
    /// </summary>
    public event EventHandler<CorePlaybackState>? StateChanged;

    /// <summary>
    /// 再生状態を変更する。状態の変更はすべてこのメソッドを通すこと。
    /// UI 側が独自に状態を持って二重管理すると、「表示は再生中なのに実際は止まっている」類の
    /// 不整合が生まれるため、変化を必ず外へ通知する。
    /// </summary>
    private void SetState(CorePlaybackState next)
    {
        lock (_stateLock)
        {
            if (_state == next) return;
            _state = next;
        }
        DiagnosticLog.Write("engine", $"状態遷移 -> {next}");
        StateChanged?.Invoke(this, next);
    }

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<PlaybackStatistics>? StatisticsUpdated;

    /// <summary>動画ファイルを開き、トラック・チャプター情報の構築と音声出力の準備までを行う。</summary>
    /// <exception cref="Exception">
    /// ファイルオープン・ストリーム解析・デコーダ初期化・音声出力初期化のいずれかが失敗した場合。
    /// FFmpeg / NAudio 由来の例外がそのまま伝播することがあるため型は限定されない。
    /// 失敗した場合、このインスタンスはファイルを開く前の状態へ巻き戻される。
    /// </exception>
    public void Open(string filePath)
    {
        Close();

        fixed (AVFormatContext** fmtCtxPtr = &_fmtCtx)
        {
            int ret = avformat_open_input(fmtCtxPtr, filePath, null, null);
            if (ret < 0)
            {
                string err = FFmpegError.Describe(ret);
                DiagnosticLog.Write("error", $"ファイルオープン失敗 path={filePath} ret={ret} ({err})");
                throw new InvalidOperationException($"Cannot open file: {filePath} (ret={ret}: {err})");
            }
        }

        try
        {
            LoadStreamsAndSetupAudio(filePath);
        }
        catch
        {
            // 途中まで構築したデコーダ・フォーマットコンテキストを残すと、次の Open や Dispose が
            // 中途半端な状態を触ることになる。呼び出し元へ投げ直す前に完全に巻き戻す。
            // 巻き戻し中の二次例外で本来の原因が消えないよう、そちらは捕まえて記録するに留める
            try { DisposeDecoders(); }
            catch (Exception cleanupEx)
            {
                DiagnosticLog.WriteFatal("error", $"ファイルオープン失敗後の巻き戻しで二次例外: {cleanupEx}");
            }
            throw;
        }
    }

    /// <summary>
    /// 開いているファイルを閉じ、デコーダ・音声出力・フォーマットコンテキストを解放して
    /// 「何も開いていない」状態へ戻す。オープンの途中で失敗した場合の後始末にも使う。
    /// </summary>
    public void Close()
    {
        Stop();
        DisposeDecoders();
    }

    private void LoadStreamsAndSetupAudio(string filePath)
    {
        // 情報が不完全でも再生自体は試みる価値があるため続行するが、後段の症状（トラックが出ない・
        // 尺が 0 になる等）の原因になるので記録は残す
        int infoRet = avformat_find_stream_info(_fmtCtx, null);
        if (infoRet < 0)
            DiagnosticLog.Write("error",
                $"ストリーム情報の取得に失敗（得られた情報のまま続行） path={filePath} ret={infoRet} ({FFmpegError.Describe(infoRet)})");

        var audioTracks = new List<AudioTrackInfo>();
        var chapters = new List<ChapterInfo>();

        // moov/trak/udta/name ボックスから OBS 等が書き込むトラック名を取得
        var mp4TrackNames = Mp4TrackNameReader.Read(filePath);

        for (int i = 0; i < (int)_fmtCtx->nb_streams; i++)
        {
            var stream = _fmtCtx->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.Video && _videoDecoder == null)
            {
                _videoDecoder = new VideoDecoder(stream, EnsureSharedHwDeviceCtx());
            }
            else if (stream->codecpar->codec_type == AVMediaType.Audio)
            {
                var decoder = new AudioDecoder(stream);
                _audioDecoders.Add(decoder);
                _audioStreamToTrack[stream->index] = _audioDecoders.Count - 1;

                var langTag = av_dict_get(stream->metadata, "language", null, 0);
                string lang = langTag != null ? Marshal.PtrToStringUTF8((IntPtr)langTag->value) ?? string.Empty : "";
                if (lang == "und") lang = string.Empty;

                // 1. moov/trak/udta/name ボックス（OBS 等が書き込む）を最優先
                mp4TrackNames.TryGetValue(stream->id, out string? udataName);

                // 2. FFmpeg stream metadata の title タグ
                var titleTag = av_dict_get(stream->metadata, "title", null, 0);
                string metaTitle = titleTag != null ? Marshal.PtrToStringUTF8((IntPtr)titleTag->value) ?? string.Empty : string.Empty;

                // 3. handler_name（汎用名は除外）
                var handlerTag = av_dict_get(stream->metadata, "handler_name", null, 0);
                string handlerName = handlerTag != null ? Marshal.PtrToStringUTF8((IntPtr)handlerTag->value) ?? string.Empty : string.Empty;
                if (handlerName is "SoundHandler" or "AudioHandler" or "Sound Media Handler")
                    handlerName = string.Empty;

                int ch = stream->codecpar->ch_layout.nb_channels;
                int sr = stream->codecpar->sample_rate;
                string codecName = avcodec_get_name(stream->codecpar->codec_id);
                string name = !string.IsNullOrEmpty(udataName) ? udataName!
                    : !string.IsNullOrEmpty(metaTitle) ? metaTitle
                    : !string.IsNullOrEmpty(handlerName) ? handlerName
                    : $"{codecName} {ch}ch {sr / 1000}kHz";

                audioTracks.Add(new AudioTrackInfo(
                    stream->index,
                    _audioDecoders.Count,
                    name, lang,
                    avcodec_get_name(stream->codecpar->codec_id),
                    stream->codecpar->ch_layout.nb_channels,
                    stream->codecpar->sample_rate));
            }
        }

        for (int i = 0; i < (int)_fmtCtx->nb_chapters; i++)
        {
            var ch = _fmtCtx->chapters[i];
            var titleTag = av_dict_get(ch->metadata, "title", null, 0);
            string title = titleTag != null ? Marshal.PtrToStringUTF8((IntPtr)titleTag->value) ?? string.Empty : $"Chapter {i + 1}";
            double startSec = ch->start * av_q2d(ch->time_base);
            chapters.Add(new ChapterInfo(i, title, TimeSpan.FromSeconds(startSec), IsUserDefined: false));
        }

        var userChapters = UserChapterStore.Load(filePath, chapters.Count, out string? chapterLoadError);
        if (chapterLoadError != null)
            DiagnosticLog.WriteFatal("chapter", $"保存済みチャプターを読めなかった（.bak へ退避した）: {chapterLoadError}");
        chapters.AddRange(userChapters);
        chapters = chapters.OrderBy(c => c.StartTime).ToList();
        _chapters = chapters.Select((c, idx) => c with { Index = idx }).ToList();

        double durationSec = _fmtCtx->duration / (double)AV_TIME_BASE;
        var videoStream = _videoDecoder != null ? _fmtCtx->streams[_videoDecoder.StreamIndex] : null;

        if (videoStream != null)
        {
            double fps = av_q2d(videoStream->avg_frame_rate);
            _videoFrameDuration = fps > 0 ? 1.0 / fps : 1.0 / 30.0;
        }

        _currentMedia = new MediaInfo
        {
            FilePath = filePath,
            Duration = TimeSpan.FromSeconds(durationSec),
            Width = videoStream != null ? videoStream->codecpar->width : 0,
            Height = videoStream != null ? videoStream->codecpar->height : 0,
            HasHdr = false,
            VideoStreamIndex = _videoDecoder?.StreamIndex ?? -1,
            AudioTracks = audioTracks,
            Chapters = _chapters
        };

        SetupAudio();
    }

    /// <summary>
    /// 共有 D3D11 デバイスを初回のみ遅延生成し、その注入用生ポインタを返す。生成に失敗した場合や
    /// GPU 無し環境では <see cref="IntPtr.Zero"/> を返し、VideoDecoder 側は従来の FFmpeg 自前生成経路へフォールバックする。
    /// </summary>
    private IntPtr EnsureGpuDevicePointer()
    {
        if (!_gpuDeviceInitAttempted)
        {
            _gpuDeviceInitAttempted = true;
            try
            {
                _gpuDevice = new GpuDeviceContext();
            }
            catch (Exception ex)
            {
                _gpuDevice = null;
                DiagnosticLog.Write("gpuDevice",
                    $"自前 D3D11 デバイス生成に失敗（従来の FFmpeg 自前生成経路へフォールバック）: {ex.Message}");
            }
        }
        return _gpuDevice?.NativeDevicePointer ?? IntPtr.Zero;
    }

    /// <summary>
    /// 共有 HW デバイスコンテキスト（FFmpeg D3D11VA）を初回のみ生成して返す。共有 D3D11 デバイスと同じく使い回すことで、
    /// ファイル切替のたびに <c>av_hwdevice_ctx_init</c>/<c>uninit</c> を繰り返してネイティブヒープを破損させる問題
    /// （連続 D&amp;D クラッシュ）を防ぐ。GPU 無し環境や生成失敗時は null を返し、VideoDecoder 側が従来の FFmpeg 自前生成経路へフォールバックする。
    /// </summary>
    private AVBufferRef* EnsureSharedHwDeviceCtx()
    {
        IntPtr devicePtr = EnsureGpuDevicePointer();
        if (devicePtr == IntPtr.Zero) return null;
        if (_sharedHwDeviceCtx == null)
            _sharedHwDeviceCtx = HardwareAccel.CreateD3D11VAContextFromDevice(devicePtr);
        return _sharedHwDeviceCtx;
    }

    private void SetupAudio()
    {
        _mixer = new MultiTrackMixer();
        _audioStates.Clear();
        foreach (var _ in _audioDecoders)
        {
            var state = new AudioTrackState();
            _audioStates.Add(state);
            _mixer.AddTrack(state);
        }

        int wasapiLatencyMs = 100;
        try
        {
            _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, wasapiLatencyMs);
            _wasapiOut.Init(_mixer);
        }
        catch (Exception ex)
        {
            // 既定の出力デバイスなし・Windows Audio サービス停止・RDP で音声リダイレクト無効、
            // といった環境で失敗する。再生位置クロックが音声出力を基準にしている都合上、
            // 音声なしでの再生は現状成立しないため、ここで意味のあるメッセージにして中断する
            DiagnosticLog.Write("error", $"音声出力デバイスの初期化に失敗: {ex}");
            _wasapiOut?.Dispose();
            _wasapiOut = null;
            throw new InvalidOperationException(
                "音声出力デバイスを初期化できませんでした。既定の再生デバイスが利用可能か確認してください。", ex);
        }

        _clock.Reset();
        _positionSource = new WasapiPositionSource(
            _wasapiOut, _wasapiOut.OutputWaveFormat, AudioDecoder.OutSampleRate,
            () => _clock.WriteCursor, wasapiLatencyMs / 1000.0);

        _mixer.OnAudioWritten = frames =>
        {
            if (Interlocked.Exchange(ref _awaitingAnchor, 0) == 1)
            {
                _clock.AnchorAt(_clock.WriteCursor, _pendingAnchorTarget);
                DiagnosticLog.Write("clock", $"anchor 確定 cursor={_clock.WriteCursor} pts={_pendingAnchorTarget:F3}");
            }
            _clock.OnAudioWritten(frames);
        };
        _mixer.OnSilenceWritten = frames => _clock.OnSilenceWritten(frames);
    }

    public void Play()
    {
        if (_fmtCtx == null) return;
        if (_state == CorePlaybackState.Playing) return;
        bool wasStopped = _state == CorePlaybackState.Stopped;
        SetState(CorePlaybackState.Playing);
        _playbackEndedFired = false;
        _lastFrameServedTicks = Environment.TickCount64;
        _lastPullTimestamp = Stopwatch.GetTimestamp();
        DiagnosticLog.Write("engine", $"Play wasStopped={wasStopped}");
        ReleaseHeldFrame();
        // EOF に到達した状態から再生を押された場合、demux は最後まで読み終えて待機しているだけなので、
        // 先頭へ巻き戻さないと何も起きない（「最後まで見た動画をもう一度再生できない」）
        // 保留中のシーク要求があるなら、EofReached がまだ true でも「終端から再開」ではない
        // （ユーザーがシークした直後に再生を押した場合。ここを見落とすと先頭へ強制的に戻してしまう）
        bool restartFromEof = _demuxThread?.EofReached == true && IsSettledAfterSeek();
        // パイプラインがまだ無い＝本当に停止状態からの新規開始。EOF で Stopped になった場合は
        // パイプラインが生きたまま残る（CheckPlaybackEnded は畳まない）ので区別できる
        bool pipelineWasFresh = _demuxThread == null;
        try
        {
            EnsurePipelineStarted();
            if (wasStopped)
            {
                // 新規再生の開始点で提示統計をリセットし、この再生1本ごとのドロップ率を UI に表示する（性能検証・実運用の可視性）。
                _droppedFrames = 0;
                _displayedFrames = 0;
                // Seek は着地後の最初の音声サンプル投入時に錨を要求するため、こちらでは要求しない。
                // 巻き戻せていない場合も Seek を使う（RequestAnchor(0.0) だと、実際の内容は
                // 停止位置からなのに表示だけ 0 秒から進むという食い違いになる）
                if (restartFromEof || _rewindSkipped)
                {
                    // 終端で音声出力を止めている間に実ハードウェア位置と書込カーソルが乖離し、
                    // 位置ソースが実クロックからフォールバック（外挿）へ切り替わっていることがある。
                    // Seek が先に表示用のガード（BeginSeek）を立てるので、その後で内部カウンタを戻す
                    Seek(TimeSpan.Zero);
                    _positionSource?.Reset();
                    _rewindSkipped = false;
                }
                else if (pipelineWasFresh) RequestAnchor(0.0);
                // 上記以外は「EOF 後に手動でシークしてから再生した」場合。そのシークが既に
                // 正しい錨を張っているので、ここで 0 秒として上書きしてはいけない
            }
            // 音声出力の開始に失敗すると audio-master クロックが進まず再生が成立しないため、
            // ここも巻き戻しの対象に含める（呼び出し元は「失敗＝再生していない」と扱うため）
            _wasapiOut?.Play();
        }
        catch
        {
            // ここで巻き戻さないと _state=Playing・_demuxThread が非 null のまま残り、
            // 以後 Play() は先頭の早期 return で、EnsurePipelineStarted は再入ガードで
            // それぞれ素通りしてしまい、アプリを再起動するまで再生できなくなる。
            // 後始末は Stop() と同じ扱いにする（片方だけ丁寧にすると、読み取り位置が
            // 不明なまま次の再生に入って表示位置がずれる経路が残る）
            SetState(CorePlaybackState.Stopped);
            HandleTeardownResult(TeardownPipeline());
            throw;
        }
    }

    public void Pause()
    {
        if (_state != CorePlaybackState.Playing) return;
        // ネイティブ呼び出しを先に済ませてから状態を確定する。逆順にすると、失敗して例外が出たときに
        // 呼び出し元は「失敗＝状態は変わっていない」と扱うのに、Engine 内部だけ Paused へ進んでしまう
        _wasapiOut?.Pause();
        SetState(CorePlaybackState.Paused);
        DiagnosticLog.Write("engine", $"Pause pos={Position.TotalSeconds:F3}");
    }

    public void Stop()
    {
        ReleaseHeldFrame();
        bool allThreadsStopped = TeardownPipeline();
        SetState(CorePlaybackState.Stopped);
        // ここまで来るとパイプラインは畳み終わっていて後戻りできない。
        // 音声デバイス側の停止に失敗しても停止状態として扱い、記録だけ残す
        try { _wasapiOut?.Stop(); }
        catch (Exception ex) { DiagnosticLog.WriteFatal("engine", $"音声出力の停止に失敗: {ex}"); }
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();
        _clock.Reset();
        _positionSource?.Reset();
        _playbackEndedFired = false;
        // シーク中断のまま Stop された場合に保留状態が次の Play() へ持ち越されないようにする
        _videoPrerollReady = true;
        _audioPrerollReady = true;
        if (_mixer != null) _mixer.HoldOutput = false;
        HandleTeardownResult(allThreadsStopped);
    }

    /// <summary>
    /// パイプラインを畳んだ後の共通の後始末。Stop() と Play() の失敗経路の両方から呼ぶこと。
    /// 片方だけ丁寧に扱うと、読み取り位置が不明なまま次の再生に入って表示位置がずれる経路が残る。
    /// </summary>
    private void HandleTeardownResult(bool allThreadsStopped)
    {
        // demux スレッドがまだ生きている可能性がある間に AVFormatContext を触ると、
        // av_read_frame と競合してネイティブヒープを壊す。その場合は巻き戻しを諦めるが、
        // 読み取り位置が不明なまま次の再生に入ると表示位置がずれるため覚えておく
        if (allThreadsStopped)
        {
            RewindToStart();
            _rewindSkipped = false;
        }
        else
        {
            _rewindSkipped = true;
            DiagnosticLog.WriteFatal("engine", "停止待ちが完了しなかったため読み取り位置の巻き戻しを省略した");
        }
    }

    /// <summary>
    /// コンテナの読み取り位置を先頭へ戻す。停止後に再生すると次の DemuxThread は
    /// この位置から読み始めるため、ここで巻き戻さないと「表示は 0:00 なのに
    /// 停止した位置の続きが流れる」「EOF 後に停止しても再生し直せない」ことになる。
    /// パイプラインを畳んだ後（demux スレッドが居ない状態）でのみ呼ぶこと。
    /// </summary>
    private void RewindToStart()
    {
        if (_fmtCtx == null) return;
        int ret = avformat_seek_file(_fmtCtx, -1, long.MinValue, 0, 0, (int)AVSEEK_FLAG.Backward);
        if (ret < 0)
            DiagnosticLog.Write("engine", $"停止時の巻き戻しに失敗 ret={ret} ({FFmpegError.Describe(ret)})");
    }

    public void Seek(TimeSpan position)
    {
        // demux スレッドが動いていない状態でシークを受け付けると、HoldOutput だけ立てて
        // 解除側（プリロール完了通知）が永久に来ず、以後の再生が音も映像も出なくなる
        if (_fmtCtx == null || _demuxThread == null)
        {
            DiagnosticLog.Write("engine", $"再生していないためシーク要求を無視 target={position.TotalSeconds:F3} state={_state}");
            return;
        }

        // 目標を [0, duration) にクランプ（スキップ連打で負値や duration 超えの目標が来る）
        double durationSec = _currentMedia?.Duration.TotalSeconds ?? 0.0;
        double target = Math.Clamp(position.TotalSeconds, 0.0, Math.Max(0.0, durationSec - 0.1));
        DiagnosticLog.Write("engine", $"Seek 要求 raw={position.TotalSeconds:F3} target={target:F3} state={_state}");

        _clock.BeginSeek(target);
        ReleaseHeldFrame();
        // ミキサーに残る旧位置の音声を即座に破棄する（シーク中に古い音が鳴り続けるのを防ぐ）。
        // クロックの錨は AudioDecodeThread が新サンプルを投入する瞬間に要求される（早期消費バグの根治）
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();

        // 映像プリロール（キーフレーム→目標地点の破棄デコード）は実時間がかかることがある。
        // 音声だけ先にプリロールを終えて実時間で再生を始めるとクロックが映像を置き去りにし、
        // 映像が追いつこうとして大量ドロップ（早送りに見える）が発生する。
        // 音声・映像の両方のプリロールが完了するまでミキサーの実音声出力を保留する
        if (_mixer != null)
        {
            _videoPrerollReady = _videoDecoder == null;
            _audioPrerollReady = _audioDecoders.Count == 0;
            _mixer.HoldOutput = true;
            DiagnosticLog.Write("gate", $"HoldOutput 設定 target={target:F3} videoQueueSerial={_videoQueue?.Serial ?? -1} audioQueueSerial={_audioQueue?.Serial ?? -1}");
        }

        int minSerial = (_videoRing?.CurrentSerial ?? 0) + 1; // これから demux が Flush で進める世代
        _demuxThread?.RequestSeek(target);
        _playbackEndedFired = false;
        _lastFrameServedTicks = Environment.TickCount64;
        _lastPullTimestamp = Stopwatch.GetTimestamp();

        // 一時停止中のシークは、着地後（＝新世代）の最初のフレームを即座に1枚だけ表示する
        if (_state == CorePlaybackState.Paused)
            TryHoldNextFrame(TimeSpan.FromMilliseconds(500), minSerial);
    }

    /// <summary>次に実音声が mixer へ書かれた瞬間、その書込カーソル位置を srcPts=target としてクロックを起点合わせする。</summary>
    private void RequestAnchor(double targetSeconds)
    {
        _pendingAnchorTarget = targetSeconds;
        Interlocked.Exchange(ref _awaitingAnchor, 1);
        DiagnosticLog.Write("clock", $"anchor 要求 target={targetSeconds:F3}");
    }

    /// <summary>音声プリロール完了時（AudioDecodeThread からのコールバック）。錨の要求と準備完了の両方を行う。</summary>
    private void OnAudioPrerollReady(double targetSeconds)
    {
        RequestAnchor(targetSeconds);
        _audioPrerollReady = true;
        DiagnosticLog.Write("gate", $"audioPrerollReady=true target={targetSeconds:F3} video={_videoPrerollReady}");
        TryReleaseMixerHold();
    }

    /// <summary>映像プリロール完了時（VideoDecodeThread からのコールバック）。</summary>
    private void OnVideoPrerollReady()
    {
        _videoPrerollReady = true;
        DiagnosticLog.Write("gate", $"videoPrerollReady=true audio={_audioPrerollReady}");
        TryReleaseMixerHold();
    }

    /// <summary>音声・映像の両方のプリロールが完了して初めて、ミキサーの実音声出力保留を解除する。</summary>
    private void TryReleaseMixerHold()
    {
        if (_videoPrerollReady && _audioPrerollReady && _mixer != null)
        {
            _mixer.HoldOutput = false;
            DiagnosticLog.Write("gate", "HoldOutput 解除");
        }
    }

    public void SetPlaybackSpeed(double speed)
    {
        double clamped = Math.Clamp(speed, 0.1, 4.0);
        _playbackSpeed = clamped;
        foreach (var d in _audioDecoders)
            d.PlaybackSpeed = clamped;

        // 境界 = 現在の書込カーソル + バッファ残量。バッファ内の旧速度 PCM が掃けた地点から新レートを適用する
        long boundary = _clock.WriteCursor + EstimateBufferedFramesAheadOfCursor();
        _clock.SetSpeedAt(boundary, clamped);
    }

    private long EstimateBufferedFramesAheadOfCursor()
    {
        if (_audioStates.Count == 0 || _mixer == null) return 0;
        int blockAlign = _mixer.WaveFormat.BlockAlign;
        int maxBufferedBytes = _audioStates.Max(s => s.Buffer.BufferedBytes);
        return maxBufferedBytes / blockAlign;
    }

    public void StepForward()
    {
        if (_state != CorePlaybackState.Paused) return;
        ReleaseHeldFrame();
        TryHoldNextFrame(TimeSpan.FromMilliseconds(500), _videoRing?.CurrentSerial ?? 0);
    }

    public void StepBackward()
    {
        if (_state != CorePlaybackState.Paused) return;
        // Position（音声クロック基準）ではなく現在表示中フレームの実PTSを基準にする。
        // StepForward は音声クロックを進めないため、Position を使うと数コマ進めた直後の
        // 戻しが「一時停止した瞬間の位置」まで戻ってしまう不具合があった
        var currentPts = _heldLease?.Pts ?? Position;
        var target = currentPts - TimeSpan.FromSeconds(_videoFrameDuration);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        Seek(target); // Paused 中なので Seek 内部で held フレームも更新される
    }

    /// <summary>
    /// 現在位置に表示すべき新しいフレームがあればリースして返す。Playing 中はクロック位置に対して
    /// due なフレームをその場でリースする。Paused 中は Step/Seek で更新された保持フレームを一度だけ返す。
    /// </summary>
    public VideoFrameLease? TryGetFrame(TimeSpan position)
    {
        if (_videoRing == null) return null;

        if (_state == CorePlaybackState.Playing)
        {
            long pullNow = Stopwatch.GetTimestamp();
            double gapSincePrevPullMs = _lastPullTimestamp == 0
                ? 0.0
                : (pullNow - _lastPullTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastPullTimestamp = pullNow;

            bool got = _videoRing.TryLeaseDue(position.TotalSeconds, _videoFrameDuration, out var lease, out int dropped);
            _droppedFrames += dropped;
            if (dropped > 0)
            {
                DiagnosticLog.Write("videoDrop",
                    $"dropped={dropped} gapSincePrevPullMs={gapSincePrevPullMs:F1} frameDurationMs={_videoFrameDuration * 1000.0:F1} clock={position.TotalSeconds:F3} ring={_videoRing.DescribeSlots()}");
            }
            if (!got || lease == null) return null;

            _displayedFrames++;
            _lastVideoLagSec = lease.Pts.TotalSeconds - position.TotalSeconds;
            _lastFrameServedTicks = Environment.TickCount64;
            return lease;
        }

        if (_heldLease is { } held && !_heldFrameConsumed)
        {
            _heldFrameConsumed = true;
            return held;
        }
        return null;
    }

    public void ReturnFrame(VideoFrameLease lease)
    {
        // Paused 中に保持しているフレーム（_heldLease）だけは Step/Seek/Play で入れ替えるまで手放さない。
        // それ以外は必ず返却する。以前は「Playing 中のみ返却」だったため、TryGetFrame と
        // ReturnFrame の間に Pause 等の状態遷移が挟まるとリースが漏れ、4スロット枯渇で映像が止まった
        if (_heldLease != null && lease.SlotIndex == _heldLease.SlotIndex) return;
        _videoRing?.ReturnLease(lease.SlotIndex);
    }

    private void ReleaseHeldFrame()
    {
        if (_heldLease is { } held)
            _videoRing?.ReturnLease(held.SlotIndex);
        _heldLease = null;
        _heldFrameConsumed = true;
    }

    private void TryHoldNextFrame(TimeSpan timeout, int minSerial)
    {
        if (_videoRing == null) return;
        if (!_videoRing.TryLeaseOldest(timeout, minSerial, out var lease) || lease == null) return;

        _heldLease = lease;
        _heldFrameConsumed = false;
        PositionChanged?.Invoke(this, _heldLease.Pts);
    }

    public void SetTrackVolume(int trackNumber, float volume)
    {
        int idx = trackNumber - 1;
        if (idx >= 0 && idx < _audioStates.Count)
            _audioStates[idx].Volume = Math.Clamp(volume, 0f, 2f);
    }

    public void SetTrackMute(int trackNumber, bool muted)
    {
        int idx = trackNumber - 1;
        DiagnosticLog.Write("engine", $"SetTrackMute track={trackNumber} muted={muted} state={_state}");
        if (idx >= 0 && idx < _audioStates.Count)
            _audioStates[idx].IsMuted = muted;
    }

    public void SetMasterVolume(float volume) => _mixer?.SetMasterVolume(volume);

    public IReadOnlyList<ChapterInfo> GetChapters() => _chapters;

    public void JumpToChapter(int index)
    {
        if (index < 0 || index >= _chapters.Count) return;
        Seek(_chapters[index].StartTime);
    }

    public void JumpToPreviousChapter()
    {
        var pos = Position;
        var prev = _chapters.LastOrDefault(c => c.StartTime < pos - TimeSpan.FromSeconds(1));
        if (prev != null) Seek(prev.StartTime);
    }

    public void JumpToNextChapter()
    {
        var pos = Position;
        var next = _chapters.FirstOrDefault(c => c.StartTime > pos);
        if (next != null) Seek(next.StartTime);
    }

    public void AddUserChapter(ChapterInfo chapter)
    {
        _chapters.Add(chapter);
        _chapters = _chapters.OrderBy(c => c.StartTime).Select((c, i) => c with { Index = i }).ToList();
        SaveUserChapters();
    }

    public void RemoveUserChapter(ChapterInfo chapter)
    {
        _chapters.Remove(chapter);
        _chapters = _chapters.Select((c, i) => c with { Index = i }).ToList();
        SaveUserChapters();
    }

    // 保存に失敗しても再生は続けるが、無言だと「編集したのに次回消えている」原因が追えない。
    // デバッグモードが無効でも残るよう WriteFatal で記録する
    private void SaveUserChapters()
    {
        if (_currentMedia == null) return;
        if (!UserChapterStore.Save(_currentMedia.FilePath, _chapters, out string? error))
            DiagnosticLog.WriteFatal("chapter", $"チャプターの保存に失敗: {error}");
    }

    public ChapterInfo? FindUserChapterNear(TimeSpan position, TimeSpan tolerance)
        => _chapters.FirstOrDefault(c =>
            c.IsUserDefined &&
            Math.Abs((c.StartTime - position).TotalSeconds) <= tolerance.TotalSeconds);

    public void RenameUserChapter(ChapterInfo chapter, string newTitle)
    {
        if (!chapter.IsUserDefined) return;
        var idx = _chapters.IndexOf(chapter);
        if (idx < 0) return;
        _chapters[idx] = chapter with { Title = newTitle };
        SaveUserChapters();
    }

    // ── パイプライン構築・分解 ──

    private void EnsurePipelineStarted()
    {
        if (_demuxThread != null) return; // 既に構築済み
        if (_fmtCtx == null) return;

        int videoStreamIndex = _videoDecoder?.StreamIndex ?? -1;
        int trackCount = Math.Max(1, _audioDecoders.Count);

        _videoQueue = new VideoPacketQueue(maxCount: 512, maxBytes: 40 * 1024 * 1024);
        _audioQueue = new AudioPacketQueue(maxCount: 256 * trackCount, maxBytes: 4 * 1024 * 1024 * trackCount);

        // HW デコード（D3D11VA）かつ VideoProcessor が使える環境なら GPU ゼロコピー経路、
        // そうでなければ従来の CPU（sws_scale）経路のリング・書き込み戦略（sink）を構築する。
        IVideoFrameSink? videoSink = BuildVideoRingAndSink();

        _demuxThread = new DemuxThread(
            _fmtCtx, videoStreamIndex, _audioStreamToTrack,
            _videoQueue, _audioQueue, PublishSeekTarget);

        if (_videoDecoder != null && videoSink != null)
            _videoDecodeThread = new VideoDecodeThread(
                _videoDecoder, _videoQueue, videoSink,
                () => _demuxThread!.PtsSyncOffset, _videoFrameDuration,
                onFirstFrameAfterFlush: OnVideoPrerollReady);

        _audioDecodeThread = new AudioDecodeThread(
            _audioDecoders, _audioStates, _audioQueue, () => _demuxThread!.PtsSyncOffset,
            onFirstSamplesAfterFlush: OnAudioPrerollReady);

        if (_mixer != null)
        {
            var audioThread = _audioDecodeThread;
            _mixer.OnRead = () => audioThread.Wake();
        }

        _demuxThreadHandle = StartBackgroundThread(_demuxThread.Run);
        if (_videoDecodeThread != null)
            _videoDecodeThreadHandle = StartBackgroundThread(_videoDecodeThread.Run);
        _audioDecodeThreadHandle = StartBackgroundThread(_audioDecodeThread.Run);
        _statusTimer ??= new Timer(_ => StatusTick(), null, 100, 100);

        StartVideoOutputIfPossible();
    }

    /// <summary>
    /// 映像デコーダの HW/SW 実効性に応じて、フレームリング（<see cref="_videoRing"/>）と書き込み戦略（sink）を構築する。
    /// HW デコード（D3D11VA）かつ VideoProcessor 利用可なら GPU ゼロコピー経路、そうでなければ CPU（sws_scale）経路。
    /// 映像ストリームが無い場合は null を返す（リングも作らない）。
    /// </summary>
    private IVideoFrameSink? BuildVideoRingAndSink()
    {
        if (_videoDecoder == null)
        {
            _videoRing = null;
            _videoConverter = null;
            return null;
        }

        if (_videoDecoder.IsHardwareAccelerated && _gpuDevice?.VideoDevice != null && _gpuDevice?.VideoContext != null)
        {
            var gpuRing = new GpuVideoFrameRing(_gpuDevice);
            var converter = new GpuFrameConverter(_gpuDevice);
            _videoRing = gpuRing;
            _videoConverter = converter;
            DiagnosticLog.Write("gpuConvert", "映像リング=GPU ゼロコピー経路（HW デコード + VideoProcessor）");
            return new GpuFrameSink(_videoDecoder, gpuRing, converter);
        }

        var cpuRing = new VideoFrameRing();
        _videoRing = cpuRing;
        _videoConverter = null;
        DiagnosticLog.Write("gpuConvert",
            $"映像リング=CPU 経路（hwAccel={_videoDecoder.IsHardwareAccelerated} " +
            $"videoDevice={(_gpuDevice?.VideoDevice != null ? "有" : "無")}）");
        return new CpuFrameSink(_videoDecoder, cpuRing);
    }

    private static Thread StartBackgroundThread(ThreadStart action)
    {
        var thread = new Thread(action) { IsBackground = true };
        thread.Start();
        return thread;
    }

    // ── 映像出力（案Y: スワップチェーン + vout スレッド。GPU デコード経路のみ）──

    /// <summary>
    /// 映像出力先の子ウィンドウ（HWND）を接続する。HW デコード（GPU リング）経路のときのみ、次の再生開始で
    /// この HWND にスワップチェーンを張り、専用 vout スレッドが vsync（waitable object）で Present する。
    /// </summary>
    public void AttachVideoOutput(IntPtr hwnd) => _videoOutputHwnd = hwnd;

    /// <summary>映像出力先の HWND を切り離す。</summary>
    public void DetachVideoOutput() => _videoOutputHwnd = IntPtr.Zero;

    /// <summary>vout（スワップチェーン提示）が稼働中か。UI 側はこの間、CompositionTarget.Rendering での映像プルを行わない。</summary>
    public bool IsVideoOutputActive => _swapPresenter != null;

    /// <summary>GPU デコード経路かつ HWND 接続済みなら、映像サイズでスワップチェーンを張り vout スレッドを起動する。</summary>
    private void StartVideoOutputIfPossible()
    {
        if (_videoRing is not GpuVideoFrameRing) return; // GPU 経路のみ（CPU 経路は従来の UI プル）
        if (_videoOutputHwnd == IntPtr.Zero || _gpuDevice == null) return;
        if (_currentMedia == null || _currentMedia.Width <= 0 || _currentMedia.Height <= 0) return;

        try
        {
            _swapPresenter = new SwapChainVideoPresenter(
                _gpuDevice, _videoOutputHwnd, _currentMedia.Width, _currentMedia.Height);
        }
        catch (Exception ex)
        {
            _swapPresenter = null;
            DiagnosticLog.Write("d3dPresenter", $"swapchain 生成失敗（vout 無効・UI プル経路へフォールバック）: {ex.Message}");
            return;
        }

        _voutRunning = true;
        _lastVoutPull = 0;
        _voutThreadHandle = StartBackgroundThread(VideoOutputLoop);
        DiagnosticLog.Write("d3dPresenter", "vout スレッド開始");
    }

    /// <summary>
    /// vout スレッド本体。vsync（waitable object）ごとに起床し、再生中はクロックに対して due なフレームを
    /// リースしてバックバッファへコピー・Present する。UI 合成に依存しないためフレーム間引きが起きにくい。
    /// </summary>
    private void VideoOutputLoop()
    {
        var presenter = _swapPresenter;
        if (presenter == null) return;
        if (_videoRing is not GpuVideoFrameRing ring)
        {
            // 通常は StartVideoOutputIfPossible が GPU リング以外で presenter を作らないため到達しないが、
            // presenter だけ生成された異常時もここで確実に解放して漏らさない。
            presenter.Dispose();
            return;
        }

        int currentSlot = -1;     // 現在 backbuffer に出しているスロット
        bool ownedByVout = false; // vout 自身がリース中で、返却責任が vout 側にあるスロットか
        var prevState = _state;  // 直前ループの状態（Paused→Playing 遷移の検出用）

        try
        {
            while (_voutRunning)
            {
                presenter.WaitForVBlank();
                if (!_voutRunning) break;

                long pullNow = Stopwatch.GetTimestamp();
                double gapMs = _lastVoutPull == 0 ? 0.0 : (pullNow - _lastVoutPull) * 1000.0 / Stopwatch.Frequency;
                _lastVoutPull = pullNow;

                var state = _state;

                if (state == CorePlaybackState.Playing)
                {
                    if (prevState != CorePlaybackState.Playing)
                    {
                        // Paused/Stopped → Playing 遷移直後。Pause 中に vout 自身が保持し続けていたスロットは
                        // ここで返却し、currentSlot は一旦無効化する。Play() 側の ReleaseHeldFrame が
                        // _heldLease のスロットを既に Free 化している場合があり、それをリセットせず
                        // currentSlot に残したまま下の due 判定に進むと、Free 化されデコードスレッドに
                        // 上書きされ得るスロットをそのまま誤って Render してしまう恐れがあるため。
                        if (ownedByVout && currentSlot >= 0) ring.ReturnLease(currentSlot);
                        currentSlot = -1;
                        ownedByVout = false;
                    }

                    double clock = GetMasterClockSeconds();
                    if (ring.TryLeaseDue(clock, _videoFrameDuration, out var lease, out int dropped) && lease != null)
                    {
                        _droppedFrames += dropped;
                        if (dropped > 0)
                            DiagnosticLog.Write("videoDrop",
                                $"dropped={dropped} gapMs={gapMs:F1} frameDurationMs={_videoFrameDuration * 1000.0:F1} clock={clock:F3} ring={ring.DescribeSlots()}");

                        if (ownedByVout && currentSlot >= 0) ring.ReturnLease(currentSlot);
                        currentSlot = lease.SlotIndex;
                        ownedByVout = true;
                        _displayedFrames++;
                        _lastFrameServedTicks = Environment.TickCount64;
                        _lastVideoLagSec = lease.Pts.TotalSeconds - clock;
                    }
                    // due 無し: currentSlot を維持し、下で前フレームを再提示する。
                }
                else
                {
                    // Paused/Stopped: Step/Seek で _heldLease が更新されていればそちらへ乗り換える。
                    // まだ _heldLease が無ければ（Pause 直後で Step/Seek 未実行）、Playing 中に vout が
                    // リースしていたスロットを返却せずそのまま保持・提示し続ける。ここで即返却して
                    // currentSlot を -1 にすると、次に Render すべきフレームが無いまま Present だけが
                    // 続き、FlipDiscard の2枚バックバッファが交互に出て映像が震えて見える不具合があった。
                    if (_heldLease is { Kind: FrameKind.Gpu } held)
                    {
                        if (ownedByVout && currentSlot >= 0 && currentSlot != held.SlotIndex)
                            ring.ReturnLease(currentSlot);
                        currentSlot = held.SlotIndex;
                        ownedByVout = false;
                    }
                    else if (!ownedByVout)
                    {
                        currentSlot = -1;
                    }
                }

                prevState = state;

                // 停止要求後は Render/Present に入らず即脱出する（破棄途中の swapchain を触らせない）。
                if (!_voutRunning) break;

                if (currentSlot >= 0)
                    presenter.Render(ring, currentSlot);

                // frame latency waitable object は「待機と Present が 1:1」でないと枯渇してブロックする。
                // そのため due が無い vsync でも必ず Present する（前フレームを再提示する）。
                presenter.Present();
            }

            if (ownedByVout && currentSlot >= 0) ring.ReturnLease(currentSlot);
        }
        catch (Exception ex)
        {
            // D3D 提示中の想定外例外で、専用スレッドの未処理例外→プロセス fail-fast に巻き込まれないようにする。
            DiagnosticLog.Write("d3dPresenter", $"vout スレッド異常終了（映像提示を停止）: {ex}");
        }
        finally
        {
            // swapchain の破棄は所有する vout スレッド自身が行う。メイン側(StopVideoOutput)は Join するだけで
            // Dispose しないため、Present の vsync 待ちで Join がタイムアウトしても「破棄済み swapchain を
            // ゾンビ vout が触る」レースが原理的に発生しない。
            presenter.Dispose();
        }
    }

    /// <summary>vout スレッドを停止する（リング破棄より先に呼ぶこと）。スワップチェーンの破棄は vout スレッド自身に委譲する。</summary>
    private void StopVideoOutput()
    {
        _voutRunning = false;
        var handle = _voutThreadHandle;
        if (handle != null)
        {
            // swapchain の破棄は vout スレッドの finally が行う。Join できれば破棄も完了している。
            // Present の vsync 待ちで稀に時間がかかるため長めに待つ。タイムアウト時もメインからは Dispose せず
            // （ゾンビが握るオブジェクトを消さない）、スレッド復帰後の自己破棄に委ねる。
            if (!handle.Join(TimeSpan.FromSeconds(5)))
                DiagnosticLog.Write("d3dPresenter", "vout スレッドの停止待ちがタイムアウト（swapchain 破棄はスレッド側に委譲）");
        }
        _voutThreadHandle = null;
        // 参照だけ手放す。実体の破棄は vout スレッドの finally が担う。
        _swapPresenter = null;
    }

    // demux スレッドがシーク実行直後（各キューへ FlushMarker を入れる前）に呼ぶ
    private void PublishSeekTarget(double normalizedTargetSeconds)
    {
        // これから videoQueue.Flush()/audioQueue.Flush() が発行する Flush 番兵自身の Serial を先読みする
        // （Flush() は呼ばれるたびに Serial をちょうど+1して即座にその番兵を積むため確定的に計算できる）。
        // 短時間に複数回シークされて前の Flush 番兵が後続の Flush() の Clear() で消えても、
        // 生き残った番兵は必ず自分の Serial に対応する正しい目標値を引けるようにするための紐付け
        int videoTargetSerial = (_videoQueue?.Serial ?? 0) + 1;
        int audioTargetSerial = (_audioQueue?.Serial ?? 0) + 1;
        _videoDecodeThread?.SetSeekTarget(videoTargetSerial, normalizedTargetSeconds);
        _audioDecodeThread?.SetSeekTarget(audioTargetSerial, normalizedTargetSeconds);
        // リングを demux スレッド側から即時 Flush する。これが無いと、リング満杯で
        // BeginWrite ブロック中の VideoDecodeThread が FlushMarker を処理できず、
        // 後方シーク時（リング内フレームが全て「未来」になり誰も取り出さない）に
        // 音声だけ流れて映像が止まるデッドロックになる
        _videoRing?.Flush();
        DiagnosticLog.Write("demux", $"seek 処理 target={normalizedTargetSeconds:F3} ringSerial={_videoRing?.CurrentSerial ?? -1}");
    }

    /// <returns>すべてのスレッドが時間内に停止した場合 true。false のときネイティブ資源は他スレッドが触っている可能性がある。</returns>
    private bool TeardownPipeline()
    {
        // StatusTick はスレッドプールで走り _positionSource(WASAPI COM) / _videoRing(D3D) 等のネイティブ資源を
        // 触るため、以降の破棄より先に走行中コールバックの完了を待ってタイマーを止める。
        // （Change(Infinite) や引数なし Dispose は走行中コールバックを止めないため、連続ファイル切替で
        //   破棄済みネイティブ資源へアクセスしてプロセスが不正終了する原因になっていた。）
        // ただし待ちには上限があり、時間内に止まらなければ「走行中のまま破棄へ進む」ことになる。
        // その場合は上記の不正終了が起こりうるため、必ず記録を残す。
        if (_statusTimer != null)
        {
            using var timerStopped = new ManualResetEvent(false);
            _statusTimer.Dispose(timerStopped);
            if (!timerStopped.WaitOne(TimeSpan.FromSeconds(2)))
                DiagnosticLog.WriteFatal("engine", "状態タイマーが時間内に停止しなかった（走行中のまま破棄へ進む）");
            _statusTimer = null;
        }

        // vout はリング・スワップチェーンを使うため、他の停止・破棄より先に止める。
        StopVideoOutput();

        _demuxThread?.RequestStop();
        _videoDecodeThread?.RequestStop();
        _audioDecodeThread?.RequestStop();

        _videoQueue?.Close();
        _audioQueue?.Close();
        _videoRing?.Close();
        _audioDecodeThread?.Wake();

        // 停止待ちがタイムアウトした場合、そのスレッドはまだ AVFormatContext やネイティブ資源を
        // 触っている可能性がある。呼び出し元が「触ってよいか」を判断できるよう結果を返す
        bool allStopped = true;
        allStopped &= JoinOrLog(_demuxThreadHandle, "demux");
        allStopped &= JoinOrLog(_videoDecodeThreadHandle, "映像デコード");
        allStopped &= JoinOrLog(_audioDecodeThreadHandle, "音声デコード");

        _videoQueue?.DrainAndDispose();
        _audioQueue?.DrainAndDispose();
        // リング（OutputView を保持）を先に破棄し、その後 enumerator/processor を持つ converter を破棄する。
        // どちらも GpuDeviceContext より先（GpuDeviceContext はエンジン破棄時に解放）。
        _videoRing?.Dispose();
        _videoConverter?.Dispose();

        _demuxThread = null;
        _videoDecodeThread = null;
        _audioDecodeThread = null;
        _videoQueue = null;
        _audioQueue = null;
        _videoRing = null;
        _videoConverter = null;
        _demuxThreadHandle = null;
        _videoDecodeThreadHandle = null;
        _audioDecodeThreadHandle = null;
        return allStopped;
    }

    /// <summary>スレッドの停止を待ち、時間内に止まらなければ記録する。</summary>
    /// <returns>停止した場合 true。</returns>
    private static bool JoinOrLog(Thread? handle, string name)
    {
        if (handle == null || handle.Join(TimeSpan.FromSeconds(3))) return true;
        DiagnosticLog.WriteFatal("engine", $"{name} スレッドが時間内に停止しなかった");
        return false;
    }

    // ── ステータス通知（100ms 周期。映像フレーム配送は UI 側の CompositionTarget.Rendering がプルする）──

    private double _lastVideoLagSec;
    private long _lastFrameServedTicks;
    private const int VideoStallThresholdMs = 2000;
    // TryGetFrame の pull 間隔計測用（ドロップ調査ログ専用）。TickCount64 は既定タイマー分解能が粗く
    // 1フレーム予算（60fps で約16.7ms）を見るには不十分なため Stopwatch を使う
    private long _lastPullTimestamp;

    private void StatusTick()
    {
        // GetPositionFrames() は呼ぶたびに内部の単調性チェック状態を更新するため、
        // 1tick につき1回だけ呼び、PositionChanged 通知とデバッグログの両方で使い回す
        long hwFrames = _positionSource?.GetPositionFrames() ?? 0;
        double posSeconds = _positionSource == null ? 0.0 : _clock.PositionAt(hwFrames);
        var pos = TimeSpan.FromSeconds(posSeconds);

        if (_state == CorePlaybackState.Playing || _state == CorePlaybackState.Paused)
        {
            // 終端に達した後もハードウェア位置は進み続けることがある（無音の再生・
            // デバイス側のバッファ処理）ので尺で抑える。ただしコンテナ申告の尺は実際の
            // 最終フレームより短いことがあるため（VFR・録画ファイル）、真に終端と判定できた
            // 後だけ抑える。再生中に抑えると「尺で止まったのに音だけ鳴り続ける」ように見える
            var duration = _currentMedia?.Duration ?? TimeSpan.Zero;
            bool clampToEnd = _playbackEndedFired && duration > TimeSpan.Zero && pos > duration;
            PositionChanged?.Invoke(this, clampToEnd ? duration : pos);
        }

        StatisticsUpdated?.Invoke(this, new PlaybackStatistics(_droppedFrames, _displayedFrames, _lastVideoLagSec));

        // 短時間の連続シーク直後にクロックが古いセグメントを指し続ける不具合の切り分け用
        if (DiagnosticLog.Enabled && _state == CorePlaybackState.Playing)
            DiagnosticLog.Write("pos", $"trace hwFrames={hwFrames} writeCursor={_clock.WriteCursor} pos={posSeconds:F3}");

        DetectVideoStall();
        CheckPlaybackEnded();
    }

    /// <summary>
    /// 再生中なのに映像フレームが一定時間配送されていない状態を検知して診断ログに残す。
    /// 「音声だけ流れて映像が止まる」系の不具合が再発した場合、リングの内部状態がここで採取される。
    /// </summary>
    private void DetectVideoStall()
    {
        if (!DiagnosticLog.Enabled) return;
        if (_state != CorePlaybackState.Playing || _videoDecoder == null || _videoRing == null) return;

        long now = Environment.TickCount64;
        if (now - _lastFrameServedTicks < VideoStallThresholdMs) return;

        DiagnosticLog.Write("stall",
            $"映像 {VideoStallThresholdMs}ms 以上停止 clock={GetMasterClockSeconds():F3} ring={_videoRing.DescribeSlots()}");
        _lastFrameServedTicks = now; // 停止継続中は 2 秒おきに記録
    }

    /// <summary>
    /// 直近のシークが demux・映像・音声のすべてへ浸透し終えたか。
    /// 再生終了の判定は「demux が終端に達した」「映像リングがドレインし切った」「各音声トラックが EOF」という、
    /// 別スレッドが独立に更新するフラグの組み合わせで決まる。世代が揃っていない途中の状態で判定すると、
    /// シーク直後に前の終端の残骸を見て誤って「再生終了」と判断してしまう
    /// （EOF から再生を押した直後に、始まった再生がすぐ止まる）。
    /// フラグを個別に突き合わせるのではなく、必ずこのメソッドを通して判断すること。
    /// </summary>
    private bool IsSettledAfterSeek()
    {
        // demux がシーク要求を抱えている／処理中
        if (_demuxThread?.HasPendingSeek == true) return false;
        // Flush 番兵は積まれたが、映像デコードスレッドがまだ消費していない。
        // リングの世代とは比較しないこと（リングの Flush は 1 回のシークで 2 度進むため
        // キューの Serial と 1:1 で対応せず、常に不一致になって再生終了を検出できなくなる）
        if (_videoQueue != null && _videoDecodeThread != null
            && _videoQueue.Serial != _videoDecodeThread.HandledSerial) return false;
        // 同じく音声側が追いついていない
        if (_audioQueue != null && _audioDecodeThread != null
            && _audioQueue.Serial != _audioDecodeThread.HandledSerial) return false;
        return true;
    }

    private void CheckPlaybackEnded()
    {
        if (_playbackEndedFired) return;
        if (_state != CorePlaybackState.Playing && _state != CorePlaybackState.Paused) return;
        if (_demuxThread == null || !_demuxThread.EofReached) return;
        if (!IsSettledAfterSeek()) return;
        if (_videoDecoder != null && (_videoRing == null || !_videoRing.IsEofDrained)) return;
        foreach (var s in _audioStates)
            if (!s.IsEof || s.Buffer.BufferedBytes > 0) return;

        _playbackEndedFired = true;
        DiagnosticLog.Write("engine", "再生終了を検出");
        // 音声出力を止めないと WASAPI が無音を出し続け、クロックも位置表示も終端を越えて
        // 進み続ける（デコードは終わっているのに再生時間だけ伸びていく）。
        // パイプライン自体はここでは畳まない（状態タイマーのコールバックから Join するのは危険なため）
        try { _wasapiOut?.Pause(); }
        catch (Exception ex) { DiagnosticLog.WriteFatal("engine", $"再生終了時の音声出力停止に失敗: {ex}"); }
        // 状態を進めないと、次に Play() を呼んでも冒頭の「既に Playing」ガードで弾かれ、
        // UI だけ「再生中」を表示したまま何も起きなくなる
        SetState(CorePlaybackState.Stopped);
        // 状態が Stopped になると StatusTick は位置を通知しなくなるため、ここで最終位置を 1 度だけ送る。
        // 送らないと、尺を超えた値のまま表示が固まる（1:05 / 1:00 のような表示になる）
        var endPosition = _currentMedia?.Duration ?? TimeSpan.Zero;
        if (endPosition > TimeSpan.Zero) PositionChanged?.Invoke(this, endPosition);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeDecoders()
    {
        _videoDecoder?.Dispose(); _videoDecoder = null;
        foreach (var d in _audioDecoders) d.Dispose();
        _audioDecoders.Clear();
        _audioStates.Clear();
        _audioStreamToTrack.Clear();
        _wasapiOut?.Dispose(); _wasapiOut = null;
        _mixer = null;
        _positionSource = null;
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) avformat_close_input(p); }
        _fmtCtx = null;
        // 破棄後に CurrentMedia が残っていると、閉じたはずのメディアのパスを使う処理
        // （既定ミュートの保存など）が成立してしまう
        _currentMedia = null;
        // 「読み取り位置が不明」は今開いているファイルに閉じた話。次のファイルへ持ち越さない
        _rewindSkipped = false;
    }

    public void Dispose()
    {
        Close();
        // 共有 HW デバイスコンテキストを解放する。内部で共有 D3D11 デバイスを Release するため、デバイス破棄より先に行う。
        if (_sharedHwDeviceCtx != null)
        {
            AVBufferRef* h = _sharedHwDeviceCtx;
            av_buffer_unref(&h);
            _sharedHwDeviceCtx = null;
        }
        // 共有 D3D11 デバイスはファイル切替で作り直さないため、エンジン破棄時に一度だけ解放する。
        _gpuDevice?.Dispose();
        _gpuDevice = null;
    }
}
