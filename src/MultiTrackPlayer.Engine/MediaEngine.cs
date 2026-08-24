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
    // 検疫時は Clear ではなく「空インスタンスへの差し替え」で旧実体を残す必要があるため readonly にしない。
    // 取り残されたデコード／demux スレッドは、コンストラクタで受け取った同一インスタンスを持ち続けており、
    // Clear するとそのスレッドのインデックスアクセスと競合する
    private List<AudioDecoder> _audioDecoders = new();
    private List<AudioTrackState> _audioStates = new();
    private Dictionary<int, int> _audioStreamToTrack = new();
    private MultiTrackMixer? _mixer;
    private WasapiOut? _wasapiOut;

    /// <summary>
    /// UI が設定したマスター音量。ファイルを開くたびに MultiTrackMixer を作り直すため、ここで
    /// 保持して SetupAudio で再適用しないと既定値（1.0）へ黙って戻る。ミュート中に別のファイルを
    /// 開くと「ミュート表示のまま音が全開で鳴る」ことになる。
    /// </summary>
    private float _masterVolume = MultiTrackMixer.DefaultMasterVolume;

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
    // パイプラインの世代。停止待ちがタイムアウトしたデコードスレッドが後から自力回復して
    // プリロール完了を通知してくると、そのコールバックは MediaEngine の「現在の」フィールドを
    // 触るため、新しいパイプラインの音声出力保留を実プリロール完了前に解除してしまう
    //（早送り・A-V ズレの再発）。各スレッドは構築時の世代を握り、一致しない通知は捨てる
    private volatile int _pipelineGeneration;
    // リングの Flush の呼び出し規約違反を記録したか。WriteFatal は数百ms ブロックするため
    // 毎シーク記録すると症状が「固まる」から「毎回のシークが遅れる」へ化けて診断しづらくなる。
    // パイプラインを作り直すたびにリセットする（DemuxThread 側の _flushViolationLogged と対称）
    private bool _ringFlushViolationLogged;
    private Timer? _statusTimer;
    private volatile bool _playbackEndedFired;

    // 案Y: 映像を子ウィンドウのスワップチェーンへ vsync Present する vout（GPU デコード経路のときのみ稼働）。
    // 稼働中は UI の CompositionTarget.Rendering プルを使わず、専用スレッドが vsync（waitable）ごとに提示する。
    private IntPtr _videoOutputHwnd;
    private SwapChainVideoPresenter? _swapPresenter;
    private Thread? _voutThreadHandle;
    private volatile bool _voutRunning;
    // vout スレッドの世代。_voutRunning はインスタンス共有の単一フィールドなので、停止待ちが
    // タイムアウトした旧 vout スレッドが Present から抜けたときに、新セッションが立てた
    // _voutRunning=true を自分への継続指示と誤認して旧スワップチェーンへ Present し続けてしまう
    //（同一 HWND を 2 つのスワップチェーンが二重駆動する）。各スレッドは開始時の世代を覚え、
    // 一致しなくなったら自分が過去の世代だと判断して抜ける
    private volatile int _voutGeneration;

    // 映像提示を畳んだときにユーザーへ出す文面。デバイス喪失とそれ以外を必ず区別する。
    //
    // vout を畳むと _swapPresenter が null になり IsVideoOutputActive が false へ落ちるため、
    // UI 側は自動的に CompositionTarget.Rendering の pull 経路（D3DImagePresenter）へ切り替わる。
    // swapchain だけが壊れてデバイスは生きている場合、この切り替えで映像は復活しうる。
    // 一方デバイス自体が失われている場合は pull 経路でも表示できない。
    // 「停止しました」と一律に案内すると前者で実態と食い違うので、文面を分ける
    private const string DeviceLostMessage =
        "映像デバイスが失われたため映像を表示できません（ファイルを開き直してください）";
    private const string VideoOutputFellBackMessage =
        "高速表示に問題が発生したため通常表示に切り替えました";
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

    // 停止待ちがタイムアウトし、パイプラインのスレッドがまだ生きている可能性がある状態。
    // このとき AVFormatContext・デコーダ・キュー・リング・変換器はそのスレッドが触りうるため、
    // 解放も再構築もしてはならない（解放すればネイティブヒープが壊れ、再構築すれば同じ
    // AVFormatContext を 2 つの demux スレッドが読むことになる）。解除はファイルを開き直したときのみ。
    private bool _threadsAbandoned;
    // 上記のため解放できなかったオブジェクトの置き場。参照を持ち続けることでファイナライザ
    //（Vortice の COM ラッパー等）経由の解放も起きないようにする。意図的なリーク
    private readonly List<object> _quarantined = new();
    // demux の I/O を中断するための門。ファイルごとに 1 つ作る
    private IoInterruptGate? _ioGate;

    /// <summary>
    /// <c>av_read_frame</c> / <c>avformat_seek_file</c> の I/O を中断させるための門。
    /// これが無いと、遅いストレージ（OneDrive のプレースホルダ・ネットワーク共有・スピンアップ中の
    /// 外付けドライブ）で demux スレッドが数十秒戻らず、停止待ちが必ずタイムアウトする。
    /// コールバックはネイティブ側が関数ポインタで保持するため、対応する AVFormatContext が生きている間は
    /// このインスタンスを GC から守る必要がある（解放できなかった場合は検疫して永久に保持する）。
    /// </summary>
    private sealed class IoInterruptGate
    {
        public volatile bool Abort;
        public AVIOInterruptCB_callback? Callback;
    }
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

    /// <summary>
    /// パイプラインのスレッドが停止しきらず、ネイティブ資源を解放できないまま取り残された状態。
    /// この間は再生を再開できない（同じ AVFormatContext を 2 つの demux スレッドが読むのを避けるため）。
    /// ファイルを開き直すと解除される。表示側はこれを見て復旧手段を案内すること。
    /// </summary>
    public bool IsPipelineQuarantined => _threadsAbandoned;

    /// <summary>
    /// 音声出力（WASAPI）が異常停止した状態。この間は再生を再開できない（音声出力を基準に
    /// 再生位置クロックを進めているため、再開しても位置と映像が進まない）。ファイルを開き直すと
    /// 解除される。OSD の一度きりの通知は見逃されうるので、表示側が操作のたびに再案内できるよう
    /// 状態としても残す。
    /// </summary>
    public bool IsAudioOutputFailed => _audioOutputFailed;

    private volatile bool _audioOutputFailed;

    public double PlaybackSpeed => _playbackSpeed;

    public TimeSpan Position
    {
        get
        {
            // 停止中に受けたシーク位置はクロックへ反映されない（Stop がクロックを 0 へ戻すため。
            // 検疫時はクロックの後始末ごと省略されるが、そのときは Seek が保持自体を断る）。
            // ここで返さないと、時間表示が 0 のままつまみだけ動いた状態になり、さらに現在位置を
            // 起点にする操作（JumpToNextChapter・Skip・チャプター追加）が先頭を基準にしてしまう
            if (PendingStartPosition is TimeSpan pending) return pending;
            if (_wasapiOut == null) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(GetMasterClockSeconds());
        }
    }

    private double GetMasterClockSeconds()
        => _positionSource == null ? 0.0 : _clock.PositionAt(_positionSource.GetPositionFrames());

    /// <summary>
    /// <see cref="PendingStartPosition"/> の「保持していない」を表す番兵。
    /// <see cref="TimeSpan"/>? は複数ワードのため、状態タイマー（スレッドプール）から
    /// <see cref="Position"/> を読まれたときに裂けた値を見せうる。<c>SeekEpoch</c> と同じ流儀で
    /// Ticks の <see cref="long"/> ＋番兵にし、<see cref="Volatile"/> で読み書きする。
    /// </summary>
    private const long NoPendingStart = -1;
    private long _pendingStartTicks = NoPendingStart;

    /// <summary>
    /// 停止中（パイプラインを畳んだ後）に受けたシーク位置。次の再生の開始位置になる。
    /// 設定は <see cref="Seek"/>、消費は <see cref="Play"/>、破棄は <see cref="Stop"/>。
    /// </summary>
    private TimeSpan? PendingStartPosition => ToPendingStart(Volatile.Read(ref _pendingStartTicks));

    private static TimeSpan? ToPendingStart(long ticks)
        => ticks == NoPendingStart ? null : TimeSpan.FromTicks(ticks);

    private void SetPendingStartPosition(TimeSpan? position)
        => Volatile.Write(ref _pendingStartTicks, position?.Ticks ?? NoPendingStart);

    /// <summary>保持している開始位置を取り出して同時に消す。</summary>
    private TimeSpan? TakePendingStartPosition()
        => ToPendingStart(Interlocked.Exchange(ref _pendingStartTicks, NoPendingStart));

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

    /// <summary>
    /// 再生を継続できない異常が起きたことを知らせる。UI スレッド以外からも発火するため、
    /// 購読側でディスパッチャへ移すこと。引数はそのままユーザーへ提示できる文面。
    /// </summary>
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler? VideoRingRebuilt;

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

        // ここから先は新しい AVFormatContext。前のファイルで取り残したスレッドが触るのは
        // 検疫した（解放していない）古いコンテキストだけなので、検疫状態を解除して再生可能に戻す
        _threadsAbandoned = false;
        InstallIoInterruptGate();

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

        // Read() で例外が起きると WASAPI はそのまま停止する。購読していないと、音が消えて
        // クロックも進まなくなった（＝映像まで止まった）理由が何ひとつ残らない
        _wasapiOut.PlaybackStopped += OnWasapiPlaybackStopped;
        // 新しい音声出力を用意できたので、前のファイルで起きた異常停止の状態は解除する
        _audioOutputFailed = false;

        // ミキサーは作り直したばかりで既定音量のため、UI が設定済みの値を再適用する
        _mixer.SetMasterVolume(_masterVolume);

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
        // 停止中に受けたシーク位置はこの再生で使い切る。持ち越すと、次に停止して再生したときに
        // 覚えのない位置から始まる
        TimeSpan? pendingStart = TakePendingStartPosition();
        // 保持位置が設定されるのは demux スレッドが居ないとき（＝Stopped）だけなので、
        // 一時停止からの再開でこれが残っているのは呼び出し規約違反。ここで捨てているため、
        // 記録しないと「覚えたはずの位置が使われない」という無言の食い違いになる
        if (!wasStopped && pendingStart is not null)
            DiagnosticLog.WriteFatal("engine",
                $"一時停止からの再開に開始位置の保持が残っていた（破棄する） pending={pendingStart.Value.TotalSeconds:F3}");
        try
        {
            EnsurePipelineStarted();
            if (wasStopped)
            {
                // 新規再生の開始点で提示統計をリセットし、この再生1本ごとのドロップ率を UI に表示する（性能検証・実運用の可視性）。
                _droppedFrames = 0;
                _displayedFrames = 0;
                // どの開始位置にするかの判断は Core 側の純ロジックへ出してテストしてある
                // （組み合わせの取り違えが「もう一度再生できない」等に直結するため）
                var start = PlaybackStartDecision.Decide(
                    wasStopped, pipelineWasFresh, restartFromEof, _rewindSkipped, pendingStart);
                switch (start.Action)
                {
                    case PlaybackStartAction.SeekTo:
                        // Seek は着地後の最初の音声サンプル投入時に錨を要求するため、こちらでは要求しない。
                        // 終端で音声出力を止めている間に実ハードウェア位置と書込カーソルが乖離し、
                        // 位置ソースが実クロックからフォールバック（外挿）へ切り替わっていることがある。
                        // Seek が先に表示用のガード（BeginSeek）を立てるので、その後で内部カウンタを戻す
                        Seek(start.Target);
                        _positionSource?.Reset();
                        _rewindSkipped = false;
                        break;
                    case PlaybackStartAction.AnchorAtStart:
                        RequestAnchor(0.0);
                        break;
                }
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
            // 既に検疫済み（＝EnsurePipelineStarted が構築を拒否した）なら畳むものは無い。
            // 再実行しても no-op だが「停止待ちが完了しなかった」ログが二重に出て調査を誤導する
            if (!_threadsAbandoned) HandleTeardownResult(TeardownPipeline());
            // 停止状態へ戻したので、利用者が選んだ開始位置も戻す（消費したままにすると、
            // 再生を押し直したときに黙って先頭から始まる）。Stop() は通っていないので残る
            SetPendingStartPosition(pendingStart);
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
        // 停止は「次の再生は先頭から」を意味する（下で RewindToStart までかける）ので、
        // 停止中のシークで覚えた開始位置はここで捨てる。Close() 経由のファイル切替も通る
        SetPendingStartPosition(null);
        ReleaseHeldFrame();
        bool allThreadsStopped = TeardownPipeline();
        SetState(CorePlaybackState.Stopped);
        _playbackEndedFired = false;
        // シーク中断のまま Stop された場合に保留状態が次の Play() へ持ち越されないようにする
        _videoPrerollReady = true;
        _audioPrerollReady = true;

        if (allThreadsStopped)
        {
            // パイプラインは畳み終わっていて後戻りできない。
            // 音声デバイス側の停止に失敗しても停止状態として扱い、記録だけ残す
            try { _wasapiOut?.Stop(); }
            catch (Exception ex) { DiagnosticLog.WriteFatal("engine", $"音声出力の停止に失敗: {ex}"); }
            foreach (var s in _audioStates) s.Buffer.ClearBuffer();
            _clock.Reset();
            _positionSource?.Reset();
            if (_mixer != null) _mixer.HoldOutput = false;
        }
        else
        {
            // 検疫時。取り残されたスレッドが _wasapiOut(WASAPI COM) や各トラックのバッファを
            // まだ触っている可能性がある。IAudioClient は並行アクセスに耐えないため、ここで
            // Stop() すると状態タイマーの GetPosition() と競合してプロセスが落ちうる。
            // 触るのはミキサー出力の保留（マネージドな volatile bool 1 つ）だけにして消音する。
            // これらの資源は DisposeDecoders が検疫し、解放しないまま次のファイルへ引き継がない
            if (_mixer != null) _mixer.HoldOutput = true;
            DiagnosticLog.WriteFatal("engine",
                "検疫中のため音声出力・バッファ・クロックの後始末を省略した（ミキサー出力の保留で消音）");
        }
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
            // 巻き戻しそのものが失敗することもある（シーク不可のストリーム・壊れたインデックス）。
            // 成否を見ずに false を立てると、読み取り位置は動いていないのに「先頭にある」と
            // 記録され、次の再生が錨だけ張って「表示は 0:00 なのに停止位置の続きが流れる」になる
            _rewindSkipped = !RewindToStart();
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
    /// <returns>
    /// 読み取り位置が先頭にあると言える場合 true。呼び出し元はこれを <c>_rewindSkipped</c> へ
    /// 反映すること。失敗を無視して「戻した」ことにすると、上記の食い違いが痕跡なく起きる。
    /// </returns>
    private bool RewindToStart()
    {
        // ファイルが無ければ戻すものも無い。次に開くファイルは先頭から読み始まる
        if (_fmtCtx == null) return true;
        int ret = avformat_seek_file(_fmtCtx, -1, long.MinValue, 0, 0, (int)AVSEEK_FLAG.Backward);
        if (ret < 0)
        {
            DiagnosticLog.Write("engine", $"停止時の巻き戻しに失敗 ret={ret} ({FFmpegError.Describe(ret)})");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 指定位置へシークする。<b>UI スレッドから同期的に呼ぶこと</b>。
    ///
    /// <para>
    /// <b>再生中以外</b>（一時停止中、および EOF 到達で停止したがパイプラインは生きている状態）では、
    /// 着地フレームを 1 枚表示するために最大 500ms 呼び出しスレッドをブロックする。シークバーの
    /// ドラッグは移動ごとにここを通るため、着地が遅い状況ではスクラブの反応が鈍くなる
    /// （スクラブ位置のフレームを見せるための意図的な待機。詳細は
    /// <see cref="TryHoldNextFrame"/> の remarks 参照）。
    /// </para>
    /// <para>
    /// <b>パイプラインを畳んだ後</b>（明示的に停止した状態）では、シークを実行せず位置だけを覚え、
    /// 次の <see cref="Play"/> の開始位置にする。<see cref="Position"/> は覚えた位置を返し、
    /// <see cref="PositionChanged"/> も発火するので、表示は実際の挙動に一致する。
    /// ただし<b>検疫中</b>（<see cref="IsPipelineQuarantined"/>）は次の再生自体が成立しないため、
    /// 覚えずに無視する。
    /// </para>
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (_fmtCtx == null)
        {
            DiagnosticLog.Write("engine", $"ファイル未オープンのためシーク要求を無視 target={position.TotalSeconds:F3} state={_state}");
            return;
        }

        // 目標を [0, duration) にクランプ（スキップ連打で負値や duration 超えの目標が来る）
        double durationSec = _currentMedia?.Duration.TotalSeconds ?? 0.0;
        double target = Math.Clamp(position.TotalSeconds, 0.0, Math.Max(0.0, durationSec - 0.1));

        // demux スレッドが動いていない状態でシーク本体を走らせると、HoldOutput だけ立てて
        // 解除側（プリロール完了通知）が永久に来ず、以後の再生が音も映像も出なくなる。
        // かつて要求を捨てていたが、シークバーのつまみは指の位置へ楽観的に動いているため
        // 「動いたように見えて何も起きない」状態になっていた。次の再生の開始位置として覚える
        var demuxThread = _demuxThread;
        if (demuxThread == null)
        {
            // 検疫中は「次の再生」自体が成立しない（EnsurePipelineStarted が構築を拒む）。
            // 覚えても使われない位置を表示へ反映すると、再生できるかのように見える。
            // UI 側も NotifyIfReopenRequired で弾いているが、エンジンを直接呼ぶ経路が
            // 増えたときのために、約束できないことはここでも引き受けない
            if (_threadsAbandoned)
            {
                DiagnosticLog.Write("engine",
                    $"検疫中のためシーク要求を無視 target={target:F3} state={_state}");
                return;
            }

            var pending = TimeSpan.FromSeconds(target);
            SetPendingStartPosition(pending);
            DiagnosticLog.Write("engine",
                $"停止中のシークを次の再生の開始位置として保持 target={target:F3} state={_state}");
            // 表示（時間・つまみ・チャプターの現在位置）を保持位置へそろえる。停止中は状態タイマーが
            // 止まっているので、ここで通知しないと時間表示だけ 0 のまま取り残される
            PositionChanged?.Invoke(this, pending);
            return;
        }

        DiagnosticLog.Write("engine", $"Seek 要求 raw={position.TotalSeconds:F3} target={target:F3} state={_state}");

        _clock.BeginSeek(target);
        // 保持フレームを手放してよいのは、それを表示に使っていない再生中だけ。
        // 再生中は vout / TryGetFrame が due なフレームを直接リースするので _heldLease は不要。
        //
        // それ以外（一時停止中・EOF 到達で Stopped へ落ちたがパイプラインは生きている状態）では
        // vout がこのリースを表示に使っている。ここで先に手放すと「表示すべきスロットが無い」状態に
        // なり、着地までが暗転として見える（FlipDiscard なので前フレームの再提示もできず、黒で
        // 塗らなければ 2 枚のバックバッファが交互に出てちらつく）。入れ替えは TryHoldNextFrame が
        // 「新しいリースを掴んでから旧リースを返す」順で行う。
        //
        // 判定は必ず消費側（VideoOutputLoop の Playing 以外の分岐が _heldLease を読む）と同じ条件に
        // そろえること。ここを「一時停止中だけ」と書くと、EOF で Stopped に落ちた状態のシークが
        // 「手放すが掴まない」経路に入り、Play を押すまで永久に暗転する
        if (_state == CorePlaybackState.Playing) ReleaseHeldFrame();
        // 映像プリロール（キーフレーム→目標地点の破棄デコード）は実時間がかかることがある。
        // 音声だけ先にプリロールを終えて実時間で再生を始めるとクロックが映像を置き去りにし、
        // 映像が追いつこうとして大量ドロップ（早送りに見える）が発生する。
        // 音声・映像の両方のプリロールが完了するまでミキサーの実音声出力を保留する。
        //
        // **これは下のバッファ破棄より先に行うこと。** 逆順にすると、空にしてから保留を立てる
        // までの隙間に音声デコードスレッドがシーク前のサンプルを書き足し、それをミキサーが
        // 保留前の状態で出力しうる（シーク直前の音が一瞬鳴る）。この窓は数命令ぶんだが、
        // UI スレッドがそこでプリエンプトされれば任意に広がる。
        //
        // 既知の穴（並べ替えとは独立に前からある）: ここから RequestSeek までの間に、前のシークの
        // プリロール完了通知が割り込むとフラグが true へ戻り TryReleaseMixerHold が保留を早期解除する。
        // OnAudioPrerollReady / OnVideoPrerollReady が照合しているのはパイプライン世代だけで、
        // シーク世代（SeekEpoch）を見ていないため。直すならあちらに世代照合を足すことになる
        if (_mixer != null)
        {
            _videoPrerollReady = _videoDecoder == null;
            _audioPrerollReady = _audioDecoders.Count == 0;
            _mixer.HoldOutput = true;
            DiagnosticLog.Write("gate", $"HoldOutput 設定 target={target:F3} videoQueueEpoch={_videoQueue?.Epoch.Value ?? -1} audioQueueEpoch={_audioQueue?.Epoch.Value ?? -1}");
        }

        // ミキサーに残る旧位置の音声を即座に破棄する（シーク中に古い音が鳴り続けるのを防ぐ）。
        // クロックの錨は AudioDecodeThread が新サンプルを投入する瞬間に要求される（早期消費バグの根治）。
        //
        // これは副作用として AudioDecodeThread の充填ゲート（残量 1 秒で待つ）の解放も兼ねている。
        // 一時停止中と音声出力の異常停止後はミキサーの Read が呼ばれず、ゲートは自力ではほどけない。
        // ここを RequestSeek より後ろへ動かす・条件付きにするなら、Flush 番兵の処理が遅れることを
        // 承知のうえで行うこと（あちらにも世代不一致での脱出を用意してあるので単独では固まらない）
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();

        // このシークで採番された世代。以前は「リングの現在世代 + 1」と予測していたが、
        // リングの Flush 回数に依存する予測だったため、シーク前の残骸フレームを掴んでしまっていた
        SeekEpoch epoch = demuxThread.RequestSeek(target);
        _playbackEndedFired = false;
        _lastFrameServedTicks = Environment.TickCount64;
        _lastPullTimestamp = Stopwatch.GetTimestamp();

        // 再生中以外のシークは、着地後の最初のフレームを即座に1枚だけ表示する。
        // 待つのは「このシークの世代」のフレームだけ（等値判定）。
        // 上の手放し条件と同じ「Playing 以外」でそろえる（片方だけ Paused 限定にすると
        // EOF 停止中に「手放すが掴まない」状態になり永久に暗転する）。
        // 完全に停止している場合はこのメソッド冒頭で早期 return しているのでここへは来ない
        if (_state != CorePlaybackState.Playing)
            TryHoldNextFrame(TimeSpan.FromMilliseconds(500), epoch);
    }

    /// <summary>次に実音声が mixer へ書かれた瞬間、その書込カーソル位置を srcPts=target としてクロックを起点合わせする。</summary>
    private void RequestAnchor(double targetSeconds)
    {
        _pendingAnchorTarget = targetSeconds;
        Interlocked.Exchange(ref _awaitingAnchor, 1);
        DiagnosticLog.Write("clock", $"anchor 要求 target={targetSeconds:F3}");
    }

    /// <summary>音声プリロール完了時（AudioDecodeThread からのコールバック）。錨の要求と準備完了の両方を行う。</summary>
    private void OnAudioPrerollReady(int generation, double targetSeconds)
    {
        if (!IsCurrentPipeline(generation, "audioPreroll")) return;
        RequestAnchor(targetSeconds);
        _audioPrerollReady = true;
        DiagnosticLog.Write("gate", $"audioPrerollReady=true target={targetSeconds:F3} video={_videoPrerollReady}");
        TryReleaseMixerHold();
    }

    /// <summary>映像プリロール完了時（VideoDecodeThread からのコールバック）。</summary>
    private void OnVideoPrerollReady(int generation)
    {
        if (!IsCurrentPipeline(generation, "videoPreroll")) return;
        _videoPrerollReady = true;
        DiagnosticLog.Write("gate", $"videoPrerollReady=true audio={_audioPrerollReady}");
        TryReleaseMixerHold();
    }

    /// <summary>
    /// 音声トラックが切り離されたことをユーザーへ伝える。記録だけでは「なぜか一部のトラックだけ
    /// 無音になった」ことが伝わらないため通知する。
    /// 停止待ちがタイムアウトして検疫された旧スレッドが後から通知してくることがあるので、
    /// プリロール完了通知と同じく現行パイプラインの世代でなければ捨てる（捨てないと、無関係な
    /// 新しいファイルの再生中に前のファイルの障害が表示される）。
    /// トラック番号は UI と同じ 1 起点にそろえる（SetTrackVolume の基準）。
    /// </summary>
    private void OnAudioTrackAbandoned(int generation, int trackIndex)
    {
        if (!IsCurrentPipeline(generation, "音声トラック切り離し")) return;
        PlaybackFailed?.Invoke(this,
            $"音声トラック {trackIndex + 1} を再生できないため切り離しました（このトラックは無音になります）");
    }

    /// <summary>
    /// デコードスレッドからの通知が現在のパイプライン世代のものか。停止待ちがタイムアウトした
    /// 旧スレッドが後から自力回復して通知してきた場合、それを現在のプリロールゲート・ミキサーへ
    /// 適用すると、新しいパイプラインの実プリロール完了前に音声出力保留が解除される
    /// （早送り・A-V ズレの再発）。世代が違う通知はここで捨てる。
    /// </summary>
    private bool IsCurrentPipeline(int generation, string what)
    {
        if (generation == _pipelineGeneration) return true;
        DiagnosticLog.Write("gate",
            $"{what} の通知を破棄（generation={generation} 現在={_pipelineGeneration}）");
        return false;
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
        // 旧フレームは手放さずに次を探す（Seek と同じ理由。入れ替えは TryHoldNextFrame が行う）
        TryHoldNextFrame(TimeSpan.FromMilliseconds(500), _videoRing?.CurrentEpoch ?? SeekEpoch.Initial);
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

    /// <param name="epoch">
    /// 欲しいフレームのシーク世代。等値で判定するため、シーク前の残骸フレームを掴まない。
    /// </param>
    /// <remarks>
    /// <b>呼び出しは UI スレッドから同期的に行うこと。</b>この待機は最大 <paramref name="timeout"/> ぶん
    /// 呼び出しスレッドをブロックする。等値で待つため、待機中に別のシークが割り込んで世代が
    /// 追い越されると空振りする。現在は <see cref="Seek"/>・<see cref="StepForward"/>・
    /// <see cref="StepBackward"/> のすべてが UI スレッド上の同期呼び出しで、この待機中に
    /// メッセージポンプが回らないため追い越しは構造的に発生しない。つまりこのメソッドは
    /// 並行呼び出しに耐性があるのではなく、<b>並行呼び出しされないことに依存している</b>。
    /// 将来シークを非同期化・再入可能化する場合はここの前提が崩れる。
    ///
    /// <para>
    /// 保持フレームの入れ替えは<b>新しいリースを掴んでから旧リースを返す</b>順で行う。逆順にすると
    /// <c>_heldLease</c> が一瞬 null になり、それを見た vout が表示対象を失って暗転する。
    /// 掴めなかった場合は旧フレームを保持したままにする（黒を出すより古い絵を残す方がまし）。
    /// </para>
    /// </remarks>
    private void TryHoldNextFrame(TimeSpan timeout, SeekEpoch epoch)
    {
        // Seek / PublishSeekTarget と同じくローカルへ捕捉してから使う（フィールドを複数回
        // 参照するとティアダウンとの競合に対する露出面が増える）
        var ring = _videoRing;
        if (ring == null) return;
        if (!ring.TryLeaseOldest(timeout, epoch, out var lease) || lease == null)
        {
            // 一時停止中のシーク・コマ送りで「映像が更新されないが原因が分からない」状態を
            // 追えるようにする（プリロールが timeout に間に合わなかった場合もここを通る）。
            // DescribeSlots はリングのロックを取って文字列を組み立てるため、
            // ログ無効時に引数として評価されないよう Enabled で囲む
            if (DiagnosticLog.Enabled)
                DiagnosticLog.Write("video",
                    $"表示用フレームの取得が空振り epoch={epoch} timeoutMs={timeout.TotalMilliseconds:F0} " +
                    $"slots={ring.DescribeSlots()}");
            // 旧フレームは保持したまま抜ける。位置表示だけが新しい位置になって絵が古いままという
            // 食い違いは残るが、暗転させて何も見えなくするより追いやすい
            return;
        }

        // 先に新しいリースを見えるようにしてから旧リースを返す（順序が逆だと 1 フレーム暗転する）
        var previous = _heldLease;
        _heldLease = lease;
        _heldFrameConsumed = false;
        if (previous != null) ring.ReturnLease(previous.SlotIndex);
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

    public void SetMasterVolume(float volume)
    {
        // ファイル切替でミキサーを作り直しても復元できるよう、要求値を保持しておく
        _masterVolume = volume;
        _mixer?.SetMasterVolume(volume);
    }

    /// <summary>
    /// WASAPI の再生が止まったときに呼ばれる。Stop()/Dispose() による正常停止では Exception が
    /// null になるため、異常停止だけを記録・通知する。ここで止まると音声が出なくなるだけでなく、
    /// audio-master クロックが進まなくなるので再生位置の表示と映像まで止まる。
    /// </summary>
    private void OnWasapiPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio はこのイベントを内部スレッドから非同期に発火する。購読解除と入れ違った旧
        // WasapiOut（ファイル切替・検疫の直後）からの通知で、新しいファイルの再生中に
        // 誤った失敗表示を出さないためのガード
        if (!ReferenceEquals(sender, _wasapiOut)) return;
        if (e.Exception == null) return;
        _audioOutputFailed = true;
        DiagnosticLog.WriteFatal("audio",
            $"音声出力が異常停止した（以降 音声・再生位置ともに進まない）: {e.Exception}");
        PlaybackFailed?.Invoke(this, "音声出力が停止しました。ファイルを開き直してください。");
    }

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
        if (_threadsAbandoned)
        {
            // 前のパイプラインのスレッドが止まりきっていない。ここで新しい DemuxThread を作ると
            // 同一の AVFormatContext を 2 つのスレッドが読み、av_read_frame の競合でネイティブヒープが
            // 壊れる。ファイルを開き直すまで再生を再開しない（落とさずに縮退させる）
            DiagnosticLog.WriteFatal("engine",
                "前回のパイプラインが停止しきっていないため再生を再開しない（ファイルを開き直すこと）");
            throw new InvalidOperationException(
                "前回の停止処理が完了していないため再生できません。ファイルを開き直してください。");
        }
        // 停止時に立てた I/O 中断フラグを下ろす。下ろさないと av_read_frame が即エラーを返し、
        // 開始直後に EOF と誤認される
        if (_ioGate != null) _ioGate.Abort = false;

        int videoStreamIndex = _videoDecoder?.StreamIndex ?? -1;
        int trackCount = Math.Max(1, _audioDecoders.Count);

        _videoQueue = new VideoPacketQueue(maxCount: 512, maxBytes: 40 * 1024 * 1024);
        _audioQueue = new AudioPacketQueue(maxCount: 256 * trackCount, maxBytes: 4 * 1024 * 1024 * trackCount);

        // HW デコード（D3D11VA）かつ VideoProcessor が使える環境なら GPU ゼロコピー経路、
        // そうでなければ従来の CPU（sws_scale）経路のリング・書き込み戦略（sink）を構築する。
        IVideoFrameSink? videoSink = BuildVideoRingAndSink();
        // リングを作り直すと GPU 共有テクスチャのハンドルが 4 枚とも変わる。ハンドルをキーに
        // キャッシュしている描画側（D3DImagePresenter）へ、破棄・再取得の合図を送る。
        // CurrentMedia の変化を代わりに使うと、同じファイルの停止→再生では発火せず取りこぼす
        VideoRingRebuilt?.Invoke(this, EventArgs.Empty);

        _demuxThread = new DemuxThread(
            _fmtCtx, videoStreamIndex, _audioStreamToTrack,
            _videoQueue, _audioQueue, PublishSeekTarget);

        // このパイプラインの世代。取り残された旧スレッドの遅延通知を弾くために使う
        int pipelineGeneration = ++_pipelineGeneration;
        // リング・キューは新規生成されて世代が Initial に戻るため、違反記録の抑制も解除する
        _ringFlushViolationLogged = false;

        if (_videoDecoder != null && videoSink != null)
            _videoDecodeThread = new VideoDecodeThread(
                _videoDecoder, _videoQueue, videoSink,
                () => _demuxThread!.PtsSyncOffset, _videoFrameDuration,
                onFirstFrameAfterFlush: () => OnVideoPrerollReady(pipelineGeneration));

        _audioDecodeThread = new AudioDecodeThread(
            _audioDecoders, _audioStates, _audioQueue, () => _demuxThread!.PtsSyncOffset,
            onFirstSamplesAfterFlush: target => OnAudioPrerollReady(pipelineGeneration, target),
            onTrackAbandoned: trackIndex => OnAudioTrackAbandoned(pipelineGeneration, trackIndex));

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
        int generation = _voutGeneration;
        _voutThreadHandle = StartBackgroundThread(() => VideoOutputLoop(generation));
        DiagnosticLog.Write("d3dPresenter", $"vout スレッド開始 generation={generation}");
    }

    /// <summary>
    /// vout スレッド本体。vsync（waitable object）ごとに起床し、再生中はクロックに対して due なフレームを
    /// リースしてバックバッファへコピー・Present する。UI 合成に依存しないためフレーム間引きが起きにくい。
    /// </summary>
    /// <param name="generation">
    /// このスレッドが担当する vout 世代。<c>_voutGeneration</c> と一致しなくなったら、自分は
    /// 停止待ちがタイムアウトした過去の世代だと判断してループを抜ける（旧スワップチェーンで
    /// Present を続けて新しい vout と同一 HWND を二重駆動しないため）。
    /// </param>
    private void VideoOutputLoop(int generation)
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
        // 映像提示を畳んだ理由。null なら正常終了（停止要求・世代交代）。
        // 「デバイスが失われた」と断定してよいのは実際にそうだった場合だけで、
        // それ以外の失敗を同じ文面にすると、ユーザーと開発者の両方を誤った原因へ誘導する
        string? stopReason = null;

        try
        {
            while (_voutRunning && generation == _voutGeneration)
            {
                if (!presenter.TryWaitForVBlank())
                {
                    stopReason = VideoOutputFellBackMessage;
                    break;
                }
                // 世代もここで見る。_voutRunning だけを見ると、停止待ちがタイムアウトした旧スレッドが
                // 新セッションの立て直した true を素通りし、この下の共有統計フィールド
                //（_lastVoutPull / _droppedFrames / _displayedFrames / _lastFrameServedTicks /
                //   _lastVideoLagSec。いずれも非ロック）を新 vout と競合しながら書き換えてしまう
                if (!_voutRunning || generation != _voutGeneration) break;

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

                // 停止要求後・世代交代後は Render/Present に入らず即脱出する
                //（破棄途中の swapchain や、新しい vout が使っている HWND を触らせない）。
                if (!_voutRunning || generation != _voutGeneration) break;

                if (currentSlot >= 0)
                {
                    presenter.Render(ring, currentSlot);
                }
                else
                {
                    // まだ 1 枚も表示すべきフレームが無い（ファイルを開いた直後・一時停止で保持フレーム無し）。
                    // Present は下で必ず呼ぶため、ここで塗らないと FlipDiscard の未初期化バックバッファが出る
                    presenter.ClearBackBuffer();
                }

                // frame latency waitable object は「待機と Present が 1:1」でないと枯渇してブロックする。
                // そのため due が無い vsync でも必ず Present する（前フレームを再提示する）。
                PresentOutcome outcome = presenter.TryPresent();
                if (outcome != PresentOutcome.Presented)
                {
                    // TDR・ドライバ更新等でデバイスが失われた場合、このスレッドで復旧はできないので畳む
                    //（swapchain の破棄は下の finally が行う）
                    stopReason = outcome == PresentOutcome.DeviceLost
                        ? DeviceLostMessage
                        : VideoOutputFellBackMessage;
                    break;
                }
            }

            if (generation != _voutGeneration)
                DiagnosticLog.Write("d3dPresenter",
                    $"vout スレッド generation={generation} が新世代({_voutGeneration})へ道を譲って終了");
            if (ownedByVout && currentSlot >= 0) ring.ReturnLease(currentSlot);
        }
        catch (Exception ex)
        {
            // D3D 提示中の想定外例外で、専用スレッドの未処理例外→プロセス fail-fast に巻き込まれないようにする。
            // Write は既定 no-op のため、映像が静かに止まる経路を無記録にしないよう WriteFatal で残す
            DiagnosticLog.WriteFatal("d3dPresenter", $"vout スレッド異常終了（映像提示を停止）: {ex}");
            // この catch は Render（GetBuffer / CopyResource）・ClearBackBuffer・リング操作・クロック取得の
            // すべてを覆っている。デバイス喪失と断定できるのは実際に失われている場合だけで、
            // リング側のレース等を「GPU デバイスの問題」に見せると調査を丸ごと誤らせる
            stopReason = presenter.IsDeviceLost ? DeviceLostMessage : VideoOutputFellBackMessage;
        }
        finally
        {
            // swapchain の破棄は所有する vout スレッド自身が行う。メイン側(StopVideoOutput)は Join するだけで
            // Dispose しないため、Present の vsync 待ちで Join がタイムアウトしても「破棄済み swapchain を
            // ゾンビ vout が触る」レースが原理的に発生しない。
            presenter.Dispose();
        }

        if (stopReason == null) return; // 停止要求・世代交代による正常終了

        // 記録は世代を問わず残す。世代が交代していても異常が起きた事実は変わらないので取りこぼさない。
        // 個別の原因は既に TryPresent / TryWaitForVBlank / catch のいずれかが WriteFatal で残しているため、
        // ここは経緯を補う Write にとどめる（WriteFatal を重ねると vout スレッドを余計に数百ms 止める）
        DiagnosticLog.Write("d3dPresenter",
            $"映像提示を停止 generation={generation} 現在世代={_voutGeneration} 理由={stopReason}");

        // ユーザーへの通知と後始末は現行世代のときだけ行う。世代が交代しているのは
        // ユーザー自身が停止・ファイル切替を操作した直後なので、そこへエラーを出しても混乱させるだけ。
        // なお「この喪失は次の vout が検出する」わけではない（_voutGeneration が進むのは
        // StopVideoOutput だけで、次の vout は後続の Play で初めて生成される）。検出は
        // 次に映像出力を始めたときまで遅れる
        if (generation != _voutGeneration) return;

        _voutRunning = false;
        // 破棄済みの presenter を指したままにすると IsVideoOutputActive が真を返し続け、
        // 「vout が動いていない＝_swapPresenter は null」という StopVideoOutput 側の不変条件が崩れる。
        // 世代一致を確認済みなので、他スレッドの後始末と競合しない
        _swapPresenter = null;
        PlaybackFailed?.Invoke(this, stopReason);
    }

    /// <summary>vout スレッドを停止する（リング破棄より先に呼ぶこと）。スワップチェーンの破棄は vout スレッド自身に委譲する。</summary>
    /// <returns>
    /// 時間内に停止した場合 true。false のとき、vout スレッドはまだ映像リングのスロットを
    /// リースしている可能性がある（リングを破棄してはならない）。
    /// </returns>
    private bool StopVideoOutput()
    {
        _voutRunning = false;
        // 世代を進める。Join がタイムアウトしても、旧スレッドは次にループ条件を評価した時点で
        // 自分が過去の世代だと気づいて抜ける（新セッションが _voutRunning を立て直しても誤認しない）
        _voutGeneration++;
        var handle = _voutThreadHandle;
        bool stopped = true;
        if (handle != null)
        {
            // swapchain の破棄は vout スレッドの finally が行う。Join できれば破棄も完了している。
            // Present の vsync 待ちで稀に時間がかかるため長めに待つ。タイムアウト時もメインからは Dispose せず
            // （ゾンビが握るオブジェクトを消さない）、スレッド復帰後の自己破棄に委ねる。
            if (!handle.Join(TimeSpan.FromSeconds(5)))
            {
                DiagnosticLog.WriteFatal("d3dPresenter", "vout スレッドの停止待ちがタイムアウト（swapchain 破棄はスレッド側に委譲）");
                stopped = false;
            }
        }
        _voutThreadHandle = null;
        // 参照だけ手放す。実体の破棄は vout スレッドの finally が担う。
        _swapPresenter = null;
        return stopped;
    }

    // demux スレッドがシーク実行直後（各キューへ Flush 番兵を入れる前）に呼ぶ。
    // epoch は DemuxThread.RequestSeek が採番した値で、この後に積まれる Flush 番兵と
    // リングのスロットに同じ値が刻まれる。予測は一切していない
    private void PublishSeekTarget(SeekEpoch epoch, double normalizedTargetSeconds)
    {
        // 短時間に複数回シークされて前の Flush 番兵が後続の Flush() の Clear() で消えても、
        // 生き残った番兵は必ず自分の世代に対応する正しい目標値を引けるようにするための紐付け
        _videoDecodeThread?.SetSeekTarget(epoch, normalizedTargetSeconds);
        _audioDecodeThread?.SetSeekTarget(epoch, normalizedTargetSeconds);
        // リングを demux スレッド側から即時 Flush する。これが無いと、リング満杯で
        // BeginWrite ブロック中の VideoDecodeThread が Flush 番兵を処理できず、
        // 後方シーク時（リング内フレームが全て「未来」になり誰も取り出さない）に
        // 音声だけ流れて映像が止まるデッドロックになる。
        // これがリングを Flush する唯一の経路（デコードスレッド側では呼ばない）
        var ring = _videoRing;
        bool ringFlushed = ring == null || ring.Flush(epoch);
        DiagnosticLog.Write("demux", $"seek 処理 target={normalizedTargetSeconds:F3} epoch={epoch}");
        // 無視されるのは呼び出し規約が破られたときだけ（採番は単調増加なので正当な世代は必ず適用される）。
        // 起きていた場合、シーク前の Ready フレームが残ったまま新世代の再生が始まる
        if (!ringFlushed && !_ringFlushViolationLogged)
        {
            _ringFlushViolationLogged = true;
            DiagnosticLog.WriteFatal("demux",
                $"リングのシーク世代が重複・巻き戻し（呼び出し規約違反。以降は記録しない） " +
                $"epoch={epoch} 現在={ring?.CurrentEpoch}");
        }
    }

    /// <returns>すべてのスレッドが時間内に停止した場合 true。false のときネイティブ資源は他スレッドが触っている可能性がある。</returns>
    private bool TeardownPipeline()
    {
        bool timerStopped = StopStatusTimer();

        // vout はリング・スワップチェーンを使うため、他の停止・破棄より先に止める。
        // vout スレッドはリングのスロットをリースし続けるため、停止の成否はリング破棄の可否に直結する
        bool voutStopped = StopVideoOutput();

        // 停止要求を先に立て、そのうえで I/O を中断する。順序が逆だと「停止要求は未設定・I/O は中断済み」
        // という隙間が生じ、demux スレッドが中断による負値の戻りをファイル終端と誤認して
        // EOF 番兵を積んでしまう（停止操作が再生完了として扱われる）
        _demuxThread?.RequestStop();
        _videoDecodeThread?.RequestStop();
        _audioDecodeThread?.RequestStop();

        _videoQueue?.Close();
        _audioQueue?.Close();
        _videoRing?.Close();
        _audioDecodeThread?.Wake();
        // I/O でブロック中の av_read_frame / avformat_seek_file を中断させる
        if (_ioGate != null) _ioGate.Abort = true;

        // 停止待ちがタイムアウトした場合、そのスレッドはまだ AVFormatContext やネイティブ資源を
        // 触っている可能性がある。破棄してよいかの判断にも、呼び出し元への戻り値にも使う。
        // 過去の停止で取り残したスレッドが居る間は、今回何も残らなかったとしても「触ってよい」とは
        // 言えない（そのスレッドが AVFormatContext を読み続けている可能性がある）
        bool allStopped = timerStopped && voutStopped && !_threadsAbandoned;
        allStopped &= JoinOrLog(_demuxThreadHandle, "demux");
        allStopped &= JoinOrLog(_videoDecodeThreadHandle, "映像デコード");
        allStopped &= JoinOrLog(_audioDecodeThreadHandle, "音声デコード");

        if (allStopped)
        {
            _videoQueue?.DrainAndDispose();
            _audioQueue?.DrainAndDispose();
            // リング（OutputView を保持）を先に破棄し、その後 enumerator/processor を持つ converter を破棄する。
            // どちらも GpuDeviceContext より先（GpuDeviceContext はエンジン破棄時に解放）。
            _videoRing?.Dispose();
            _videoConverter?.Dispose();
            // 誰も I/O 待ちで止まっていないので中断フラグを下ろす。下ろさないと
            // この後の RewindToStart（avformat_seek_file）まで中断されてしまう
            if (_ioGate != null) _ioGate.Abort = false;
        }
        else if (!_threadsAbandoned)
        {
            // 止まりきらなかったスレッドがキュー・リング・変換器をまだ触っている可能性がある。
            // 破棄すると解放済み領域への読み書きでネイティブヒープが壊れるため、破棄せず検疫する。
            // 既に検疫済みの場合は再構築を拒否しているので新たに検疫すべきものは無い
            // _ioGate は _fmtCtx と対の資源なので、検疫は DisposeDecoders 側に一本化する
            _threadsAbandoned = true;
            Quarantine(_videoQueue, _audioQueue, _videoRing, _videoConverter);
            DiagnosticLog.WriteFatal("engine",
                "パイプラインのスレッドが停止しなかったため、キュー・リング・変換器を解放せず検疫した" +
                $"（ファイルを開き直すまで再生を再開しない。検疫済みオブジェクト数={_quarantined.Count}）");
        }

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

    /// <summary>
    /// 状態タイマーを止め、走行中コールバックの完了を待つ。StatusTick はスレッドプールで走り
    /// _positionSource(WASAPI COM) / _videoRing(D3D) 等のネイティブ資源を触るため、以降の破棄より
    /// 先に完了させる必要がある（Change(Infinite) や引数なし Dispose は走行中コールバックを止めない。
    /// 連続ファイル切替で破棄済みネイティブ資源へアクセスしてプロセスが不正終了する原因になっていた）。
    /// </summary>
    /// <returns>時間内に停止した場合 true。false のとき、コールバックがまだネイティブ資源を触っている可能性がある。</returns>
    private bool StopStatusTimer()
    {
        if (_statusTimer == null) return true;

        var stopped = new ManualResetEvent(false);
        _statusTimer.Dispose(stopped);
        _statusTimer = null;
        if (stopped.WaitOne(TimeSpan.FromSeconds(2)))
        {
            stopped.Dispose();
            return true;
        }

        // まだ走行中。この後コールバックが完了すると Timer 側がこのハンドルを Set するため、
        // ここで Dispose すると破棄済みハンドルへの Set でスレッドプール側が落ちる。解放せず検疫する
        Quarantine(stopped);
        DiagnosticLog.WriteFatal("engine", "状態タイマーが時間内に停止しなかった（走行中のまま破棄へ進む）");
        return false;
    }

    /// <summary>ゾンビスレッドが触りうるため解放できないオブジェクトを、参照を保持して隔離する（意図的なリーク）。</summary>
    private void Quarantine(params object?[] items)
    {
        foreach (var item in items)
            if (item != null) _quarantined.Add(item);
    }

    /// <summary>
    /// 開いた AVFormatContext に I/O 中断コールバックを差し込む。<c>Open</c> の
    /// <c>avformat_open_input</c> 成功直後に呼ぶこと。
    /// </summary>
    private void InstallIoInterruptGate()
    {
        var gate = new IoInterruptGate();
        // ネイティブから関数ポインタで呼ばれる。デリゲートは gate 経由で生存させる（GC 回収でクラッシュする）
        gate.Callback = _ => gate.Abort ? 1 : 0;
        _fmtCtx->interrupt_callback = new AVIOInterruptCB { callback = gate.Callback, opaque = null };
        _ioGate = gate;
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
        // TryHoldNextFrame と同じくローカルへ捕捉してから使う。このメソッドは状態タイマー
        //（ThreadPool）から呼ばれる一方 _videoRing を null にするのは UI スレッドなので、
        // null チェックと実際の参照でフィールドを 2 度読むと、その間に null 化されうる
        //（通常は StopStatusTimer が実行中のコールバックの完了を待つが、その待ちが
        // タイムアウトした縮退経路では待たずに破棄が進む）
        var ring = _videoRing;
        if (_state != CorePlaybackState.Playing || _videoDecoder == null || ring == null) return;

        long now = Environment.TickCount64;
        if (now - _lastFrameServedTicks < VideoStallThresholdMs) return;

        DiagnosticLog.Write("stall",
            $"映像 {VideoStallThresholdMs}ms 以上停止 clock={GetMasterClockSeconds():F3} ring={ring.DescribeSlots()}");
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
        // Flush 番兵は積まれたが、映像デコードスレッドがまだ消費していない
        if (_videoQueue != null && _videoDecodeThread != null
            && _videoQueue.Epoch != _videoDecodeThread.HandledEpoch) return false;
        // 同じく音声側が追いついていない
        if (_audioQueue != null && _audioDecodeThread != null
            && _audioQueue.Epoch != _audioDecodeThread.HandledEpoch) return false;
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
        // 検疫経路では WasapiOut を解放せず参照だけ手放すため、解除しないと検疫済みの出力が
        // 停止イベントを発火し、新しいファイルの再生中に誤った失敗通知が出る
        if (_wasapiOut != null) _wasapiOut.PlaybackStopped -= OnWasapiPlaybackStopped;
        if (_threadsAbandoned)
        {
            // 止まりきらなかったスレッド（デコード系・vout・状態タイマーのコールバック）が、この
            // デコーダ・音声出力・AVFormatContext をまだ触っている可能性がある。解放すると解放済み領域や
            // 破棄済み COM を触らせることになるため、参照だけ手放して検疫する（意図的なリーク。
            // 次のファイルは新しい一式で動く）。特に _positionSource は _wasapiOut 自身を包んでおり、
            // StatusTick が GetPosition() を呼び続けている可能性がある
            //（StopStatusTimer がタイムアウトした状況とはまさにそれ）
            Quarantine(_videoDecoder, _wasapiOut, _mixer, _positionSource, _ioGate);
            foreach (var d in _audioDecoders) Quarantine(d);
            // これらのコレクションは、取り残されたデコード／demux スレッドがコンストラクタで受け取った
            // 同一インスタンスを保持している。Clear するとそのスレッドのインデックスアクセスと競合するため、
            // 空インスタンスへ差し替えて旧実体はそのまま残す
            _audioDecoders = new List<AudioDecoder>();
            _audioStates = new List<AudioTrackState>();
            _audioStreamToTrack = new Dictionary<int, int>();
            DiagnosticLog.WriteFatal("engine",
                "スレッドが停止しなかったため、デコーダ・音声出力・フォーマットコンテキストを解放せず検疫した" +
                $"（検疫済みオブジェクト数={_quarantined.Count}）");
        }
        else
        {
            _videoDecoder?.Dispose();
            foreach (var d in _audioDecoders) d.Dispose();
            _audioDecoders.Clear();
            _audioStates.Clear();
            _audioStreamToTrack.Clear();
            _wasapiOut?.Dispose();
            if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) avformat_close_input(p); }
        }
        _videoDecoder = null;
        _wasapiOut = null;
        _mixer = null;
        _positionSource = null;
        _fmtCtx = null;
        _ioGate = null;
        // 破棄後に CurrentMedia が残っていると、閉じたはずのメディアのパスを使う処理
        // （既定ミュートの保存など）が成立してしまう
        _currentMedia = null;
        // 「読み取り位置が不明」は今開いているファイルに閉じた話。次のファイルへ持ち越さない
        _rewindSkipped = false;
    }

    public void Dispose()
    {
        Close();
        if (_threadsAbandoned)
        {
            // 取り残したスレッドが検疫済みのリング・変換器・デコーダ経由で共有 D3D11 デバイスと
            // HW デバイスコンテキストを使い続けている可能性がある。ここで解放すると終了時に落ちるため、
            // 参照を保持したまま手放す（プロセス終了で OS が回収する）
            Quarantine(_gpuDevice);
            _gpuDevice = null;
            _sharedHwDeviceCtx = null;
            DiagnosticLog.WriteFatal("engine", "スレッドが停止しなかったため、共有 GPU デバイスを解放せず検疫した");
            return;
        }
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
