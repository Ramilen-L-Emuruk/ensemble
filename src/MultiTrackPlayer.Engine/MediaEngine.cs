using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Interfaces;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
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
    private Thread? _pacerThreadHandle;
    private volatile bool _pacerStopRequested;
    private VideoFrameData? _pendingFrame;
    private volatile bool _playbackEndedFired;
    private int _driftResetPending;

    private MediaInfo? _currentMedia;
    private CorePlaybackState _state = CorePlaybackState.Stopped;
    private double _playbackSpeed = 1.0;
    private List<ChapterInfo> _chapters = new();
    // WASAPI 出力バッファ遅延（秒）: Init() 後に取得し masterClock から引いて実出力タイミングに補正
    private double _wasapiLatencySec;
    // 映像の1フレーム時間（秒）: due 判定・プリロール猶予・フレームドロップ閾値に使用
    private double _videoFrameDuration = 1.0 / 30.0;
    // ドリフト移動平均（VLC average_t range=10 相当）: 瞬間ドリフトを平滑化して補正判定に使用。P4 で PlaybackClock 化に伴い削除予定
    private readonly DriftAverage _driftAverage = new(range: 10);
    // フレームドロップ統計（100フレームごとに StatisticsUpdated イベントで通知）
    private int _droppedFrames;
    private int _displayedFrames;
    private const int StatisticsIntervalFrames = 100;

    public MediaInfo? CurrentMedia => _currentMedia;
    public CorePlaybackState State => _state;
    public double PlaybackSpeed => _playbackSpeed;

    public TimeSpan Position
    {
        get
        {
            if (_wasapiOut == null) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((_mixer?.PlayedSamples ?? 0) / (double)AudioDecoder.OutSampleRate);
        }
    }

    public event EventHandler<VideoFrameData>? VideoFrameReady;
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
        // WasapiOut の要求レイテンシ。この値が masterClock 補正にも使われるため一か所で定義する
        int wasapiLatencyMs = 100;
        _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, wasapiLatencyMs);
        _wasapiOut.Init(_mixer);
        _wasapiLatencySec = wasapiLatencyMs / 1000.0;
    }

    public void Play()
    {
        if (_fmtCtx == null) return;
        if (_state == CorePlaybackState.Playing) return;
        _state = CorePlaybackState.Playing;
        _playbackEndedFired = false;
        EnsurePipelineStarted();
        _wasapiOut?.Play();
    }

    public void Pause()
    {
        if (_state != CorePlaybackState.Playing) return;
        _state = CorePlaybackState.Paused;
        _wasapiOut?.Pause();
    }

    public void Stop()
    {
        TeardownPipeline();
        _state = CorePlaybackState.Stopped;
        _wasapiOut?.Stop();
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();
        _driftAverage.Reset();
        _playbackEndedFired = false;
    }

    public void Seek(TimeSpan position)
    {
        if (_fmtCtx == null) return;
        _demuxThread?.RequestSeek(position.TotalSeconds);
        // 速度補正: masterClock = PlayedSamples / OutSampleRate * speed のため逆算（旧クロック。P4 で置換）
        _mixer?.SetPlayedSamples((long)(position.TotalSeconds / _playbackSpeed * AudioDecoder.OutSampleRate));
        Interlocked.Exchange(ref _driftResetPending, 1);
        _playbackEndedFired = false;
    }

    public void SetPlaybackSpeed(double speed)
    {
        _playbackSpeed = Math.Clamp(speed, 0.1, 4.0);
        foreach (var d in _audioDecoders)
            d.PlaybackSpeed = _playbackSpeed;
    }

    public void StepForward()
    {
        if (_state != CorePlaybackState.Paused) return;
        _videoRing?.TakeOldest(TimeSpan.FromMilliseconds(500), EmitSteppedFrame);
    }

    public void StepBackward()
    {
        if (_state != CorePlaybackState.Paused) return;
        var target = Position - TimeSpan.FromSeconds(_videoFrameDuration);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        Seek(target);
        _videoRing?.TakeOldest(TimeSpan.FromMilliseconds(500), EmitSteppedFrame);
    }

    private void EmitSteppedFrame(IntPtr buffer, int width, int height, int stride, double ptsSeconds)
    {
        var pixels = new byte[stride * height];
        Marshal.Copy(buffer, pixels, 0, pixels.Length);
        var frame = new VideoFrameData(pixels, width, height, TimeSpan.FromSeconds(ptsSeconds));
        VideoFrameReady?.Invoke(this, frame);
        PositionChanged?.Invoke(this, frame.Pts);
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
                () => _demuxThread!.PtsSyncOffset, _videoFrameDuration);

        _audioDecodeThread = new AudioDecodeThread(
            _audioDecoders, _audioStates, _audioQueue, () => _demuxThread!.PtsSyncOffset);

        if (_mixer != null)
        {
            var audioThread = _audioDecodeThread;
            _mixer.OnRead = () => audioThread.Wake();
        }

        _pacerStopRequested = false;
        _demuxThreadHandle = StartBackgroundThread(_demuxThread.Run);
        if (_videoDecodeThread != null)
            _videoDecodeThreadHandle = StartBackgroundThread(_videoDecodeThread.Run);
        _audioDecodeThreadHandle = StartBackgroundThread(_audioDecodeThread.Run);
        _pacerThreadHandle = StartBackgroundThread(PacerLoop);
    }

    private static Thread StartBackgroundThread(ThreadStart action)
    {
        var thread = new Thread(action) { IsBackground = true };
        thread.Start();
        return thread;
    }

    private void PublishSeekTarget(double normalizedTargetSeconds)
    {
        _videoDecodeThread?.SetSeekTarget(normalizedTargetSeconds);
        _audioDecodeThread?.SetSeekTarget(normalizedTargetSeconds);
    }

    private void TeardownPipeline()
    {
        _demuxThread?.RequestStop();
        _videoDecodeThread?.RequestStop();
        _audioDecodeThread?.RequestStop();
        _pacerStopRequested = true;

        _videoQueue?.Close();
        _audioQueue?.Close();
        _videoRing?.Close();
        _audioDecodeThread?.Wake();

        _demuxThreadHandle?.Join(TimeSpan.FromSeconds(3));
        _videoDecodeThreadHandle?.Join(TimeSpan.FromSeconds(3));
        _audioDecodeThreadHandle?.Join(TimeSpan.FromSeconds(3));
        _pacerThreadHandle?.Join(TimeSpan.FromSeconds(3));

        _videoQueue?.DrainAndDispose();
        _audioQueue?.DrainAndDispose();
        _videoRing?.Dispose();

        _demuxThread = null;
        _videoDecodeThread = null;
        _audioDecodeThread = null;
        _videoQueue = null;
        _audioQueue = null;
        _videoRing = null;
        _demuxThreadHandle = null;
        _videoDecodeThreadHandle = null;
        _audioDecodeThreadHandle = null;
        _pacerThreadHandle = null;
    }

    // ── 暫定ペーサー（P5 で CompositionTarget.Rendering によるプル駆動へ置換）──

    private void PacerLoop()
    {
        while (!_pacerStopRequested)
        {
            if (Interlocked.Exchange(ref _driftResetPending, 0) == 1)
                _driftAverage.Reset();

            if (_state == CorePlaybackState.Playing)
                PumpOneTick();

            CheckPlaybackEnded();
            Thread.Sleep(4);
        }
    }

    private void PumpOneTick()
    {
        if (_videoRing == null || _mixer == null) return;

        double masterClock = (_mixer.PlayedSamples / (double)AudioDecoder.OutSampleRate)
                            * _playbackSpeed - _wasapiLatencySec;

        bool got = _videoRing.TryConsumeDue(masterClock, _videoFrameDuration, (buf, w, h, stride, pts) =>
        {
            var pixels = new byte[stride * h];
            Marshal.Copy(buf, pixels, 0, pixels.Length);
            _pendingFrame = new VideoFrameData(pixels, w, h, TimeSpan.FromSeconds(pts));
        }, out int dropped);

        _droppedFrames += dropped;

        if (got && _pendingFrame != null)
        {
            var frame = _pendingFrame;
            double diff = frame.Pts.TotalSeconds - masterClock;
            _driftAverage.Update(diff);
            ApplyDriftCorrection(_driftAverage.Get());

            _displayedFrames++;
            VideoFrameReady?.Invoke(this, frame);
            PositionChanged?.Invoke(this, frame.Pts);
        }

        int total = _displayedFrames + _droppedFrames;
        if (total > 0 && total % StatisticsIntervalFrames == 0)
            StatisticsUpdated?.Invoke(this, new PlaybackStatistics(_droppedFrames, _displayedFrames, _driftAverage.Get()));
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

    // VLC stream_HandleDrift / aout_FiltersAdjustResampling 相当
    // swr_set_compensation で微量のサンプル追加/削除を行い、音声クロックを映像に追従させる
    // P4 で PlaybackClock（audio-master）へ置き換わり次第、ドリフト補正自体が不要になり削除予定
    private void ApplyDriftCorrection(double avgDriftSec)
    {
        const double kThreshold = 0.04;      // 40ms 超で補正開始
        const double kResetThreshold = 0.01; // 10ms 未満で補正終了
        const int kCompensationDistance = AudioDecoder.OutSampleRate / 2; // 500ms ウィンドウ
        const int kCorrectionSamples = 48;   // ≈ 0.1% of OutSampleRate (≈1ms per 500ms)

        if (Math.Abs(avgDriftSec) > kThreshold)
        {
            int sampleDelta = Math.Sign(avgDriftSec) * kCorrectionSamples;
            foreach (var d in _audioDecoders)
                d.SetDriftCompensation(sampleDelta, kCompensationDistance);
        }
        else if (Math.Abs(avgDriftSec) < kResetThreshold)
        {
            foreach (var d in _audioDecoders)
                d.SetDriftCompensation(0, AudioDecoder.OutSampleRate);
        }
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
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) avformat_close_input(p); }
        _fmtCtx = null;
    }

    public void Dispose() { Stop(); DisposeDecoders(); }
}
