using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Interfaces;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Pipeline;
using MultiTrackPlayer.Engine.Sync;
using MultiTrackPlayer.Engine.Video;
using NAudio.Wave;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;
using System.Runtime.InteropServices;
using CorePlaybackState = MultiTrackPlayer.Core.Enums.PlaybackState;

namespace MultiTrackPlayer.Engine;

public unsafe class MediaEngine : IMediaEngine
{
    private AVFormatContext* _fmtCtx;
    private VideoDecoder? _videoDecoder;
    private readonly List<AudioDecoder> _audioDecoders = new();
    private readonly List<AudioTrackState> _audioStates = new();
    private readonly Dictionary<int, int> _audioStreamToTrack = new();
    private MultiTrackMixer? _mixer;
    private WasapiOut? _wasapiOut;

    // ffplay 型パイプライン: demux/デコードは各専用スレッドが担当し、AVFormatContext は DemuxThread が唯一専有する
    private VideoPacketQueue? _videoQueue;
    private AudioPacketQueue? _audioQueue;
    private VideoFrameRing? _videoRing;
    private DemuxThread? _demuxThread;
    private VideoDecodeThread? _videoDecodeThread;
    private AudioDecodeThread? _audioDecodeThread;
    private Thread? _demuxThreadHandle;
    private Thread? _videoDecodeThreadHandle;
    private Thread? _audioDecodeThreadHandle;
    private Timer? _statusTimer;
    private volatile bool _playbackEndedFired;

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
    private CorePlaybackState _state = CorePlaybackState.Stopped;
    private double _playbackSpeed = 1.0;
    private List<ChapterInfo> _chapters = new();
    // 映像の1フレーム時間（秒）: due 判定・プリロール猶予・フレームドロップ閾値に使用
    private double _videoFrameDuration = 1.0 / 30.0;
    // フレームドロップ統計（100フレームごとに StatisticsUpdated イベントで通知）
    private int _droppedFrames;
    private int _displayedFrames;

    public MediaInfo? CurrentMedia => _currentMedia;
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

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<PlaybackStatistics>? StatisticsUpdated;

    public void Open(string filePath)
    {
        Stop();
        DisposeDecoders();

        fixed (AVFormatContext** fmtCtxPtr = &_fmtCtx)
        {
            int ret = avformat_open_input(fmtCtxPtr, filePath, null, null);
            if (ret < 0) throw new InvalidOperationException($"Cannot open file: {filePath}");
        }
        avformat_find_stream_info(_fmtCtx, null);

        var audioTracks = new List<AudioTrackInfo>();
        var chapters = new List<ChapterInfo>();

        // moov/trak/udta/name ボックスから OBS 等が書き込むトラック名を取得
        var mp4TrackNames = Mp4TrackNameReader.Read(filePath);

        for (int i = 0; i < (int)_fmtCtx->nb_streams; i++)
        {
            var stream = _fmtCtx->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.Video && _videoDecoder == null)
            {
                _videoDecoder = new VideoDecoder(stream);
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

        var userChapters = UserChapterStore.Load(filePath, chapters.Count);
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
        _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, wasapiLatencyMs);
        _wasapiOut.Init(_mixer);

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
        _state = CorePlaybackState.Playing;
        _playbackEndedFired = false;
        _lastFrameServedTicks = Environment.TickCount64;
        DiagnosticLog.Write("engine", $"Play wasStopped={wasStopped}");
        ReleaseHeldFrame();
        EnsurePipelineStarted();
        if (wasStopped)
            RequestAnchor(0.0);
        _wasapiOut?.Play();
    }

    public void Pause()
    {
        if (_state != CorePlaybackState.Playing) return;
        _state = CorePlaybackState.Paused;
        DiagnosticLog.Write("engine", $"Pause pos={Position.TotalSeconds:F3}");
        _wasapiOut?.Pause();
    }

    public void Stop()
    {
        ReleaseHeldFrame();
        TeardownPipeline();
        _state = CorePlaybackState.Stopped;
        _wasapiOut?.Stop();
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();
        _clock.Reset();
        _positionSource?.Reset();
        _playbackEndedFired = false;
        // シーク中断のまま Stop された場合に保留状態が次の Play() へ持ち越されないようにする
        _videoPrerollReady = true;
        _audioPrerollReady = true;
        if (_mixer != null) _mixer.HoldOutput = false;
    }

    public void Seek(TimeSpan position)
    {
        if (_fmtCtx == null) return;

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
        var target = Position - TimeSpan.FromSeconds(_videoFrameDuration);
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
            bool got = _videoRing.TryLeaseDue(position.TotalSeconds, _videoFrameDuration, out var raw, out int dropped);
            _droppedFrames += dropped;
            if (!got) return null;

            _displayedFrames++;
            _lastVideoLagSec = raw.PtsSeconds - position.TotalSeconds;
            _lastFrameServedTicks = Environment.TickCount64;
            return new VideoFrameLease(raw.SlotIndex, raw.Buffer, raw.Width, raw.Height, raw.Stride, TimeSpan.FromSeconds(raw.PtsSeconds));
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
        if (!_videoRing.TryLeaseOldest(timeout, minSerial, out var raw)) return;

        _heldLease = new VideoFrameLease(raw.SlotIndex, raw.Buffer, raw.Width, raw.Height, raw.Stride, TimeSpan.FromSeconds(raw.PtsSeconds));
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
        if (_currentMedia != null)
            UserChapterStore.Save(_currentMedia.FilePath, _chapters);
    }

    public void RemoveUserChapter(ChapterInfo chapter)
    {
        _chapters.Remove(chapter);
        _chapters = _chapters.Select((c, i) => c with { Index = i }).ToList();
        if (_currentMedia != null)
            UserChapterStore.Save(_currentMedia.FilePath, _chapters);
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
        if (_currentMedia != null)
            UserChapterStore.Save(_currentMedia.FilePath, _chapters);
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
        _videoRing = new VideoFrameRing();

        _demuxThread = new DemuxThread(
            _fmtCtx, videoStreamIndex, _audioStreamToTrack,
            _videoQueue, _audioQueue, PublishSeekTarget);

        if (_videoDecoder != null)
            _videoDecodeThread = new VideoDecodeThread(
                _videoDecoder, _videoQueue, _videoRing,
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
    }

    private static Thread StartBackgroundThread(ThreadStart action)
    {
        var thread = new Thread(action) { IsBackground = true };
        thread.Start();
        return thread;
    }

    // demux スレッドがシーク実行直後（各キューへ FlushMarker を入れる前）に呼ぶ
    private void PublishSeekTarget(double normalizedTargetSeconds)
    {
        _videoDecodeThread?.SetSeekTarget(normalizedTargetSeconds);
        _audioDecodeThread?.SetSeekTarget(normalizedTargetSeconds);
        // リングを demux スレッド側から即時 Flush する。これが無いと、リング満杯で
        // BeginWrite ブロック中の VideoDecodeThread が FlushMarker を処理できず、
        // 後方シーク時（リング内フレームが全て「未来」になり誰も取り出さない）に
        // 音声だけ流れて映像が止まるデッドロックになる
        _videoRing?.Flush();
        DiagnosticLog.Write("demux", $"seek 処理 target={normalizedTargetSeconds:F3} ringSerial={_videoRing?.CurrentSerial ?? -1}");
    }

    private void TeardownPipeline()
    {
        _demuxThread?.RequestStop();
        _videoDecodeThread?.RequestStop();
        _audioDecodeThread?.RequestStop();
        _statusTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        _videoQueue?.Close();
        _audioQueue?.Close();
        _videoRing?.Close();
        _audioDecodeThread?.Wake();

        _demuxThreadHandle?.Join(TimeSpan.FromSeconds(3));
        _videoDecodeThreadHandle?.Join(TimeSpan.FromSeconds(3));
        _audioDecodeThreadHandle?.Join(TimeSpan.FromSeconds(3));

        _videoQueue?.DrainAndDispose();
        _audioQueue?.DrainAndDispose();
        _videoRing?.Dispose();
        _statusTimer?.Dispose();

        _demuxThread = null;
        _videoDecodeThread = null;
        _audioDecodeThread = null;
        _videoQueue = null;
        _audioQueue = null;
        _videoRing = null;
        _demuxThreadHandle = null;
        _videoDecodeThreadHandle = null;
        _audioDecodeThreadHandle = null;
        _statusTimer = null;
    }

    // ── ステータス通知（100ms 周期。映像フレーム配送は UI 側の CompositionTarget.Rendering がプルする）──

    private double _lastVideoLagSec;
    private long _lastFrameServedTicks;
    private const int VideoStallThresholdMs = 2000;

    private void StatusTick()
    {
        // GetPositionFrames() は呼ぶたびに内部の単調性チェック状態を更新するため、
        // 1tick につき1回だけ呼び、PositionChanged 通知とデバッグログの両方で使い回す
        long hwFrames = _positionSource?.GetPositionFrames() ?? 0;
        double posSeconds = _positionSource == null ? 0.0 : _clock.PositionAt(hwFrames);
        var pos = TimeSpan.FromSeconds(posSeconds);

        if (_state == CorePlaybackState.Playing || _state == CorePlaybackState.Paused)
            PositionChanged?.Invoke(this, pos);

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

    private void CheckPlaybackEnded()
    {
        if (_playbackEndedFired) return;
        if (_state != CorePlaybackState.Playing && _state != CorePlaybackState.Paused) return;
        if (_demuxThread == null || !_demuxThread.EofReached) return;
        if (_videoDecoder != null && (_videoRing == null || !_videoRing.IsEofDrained)) return;
        foreach (var s in _audioStates)
            if (!s.IsEof || s.Buffer.BufferedBytes > 0) return;

        _playbackEndedFired = true;
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
    }

    public void Dispose() { Stop(); DisposeDecoders(); }
}
