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
                _clock.AnchorAt(_clock.WriteCursor, _pendingAnchorTarget);
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
    }

    public void Seek(TimeSpan position)
    {
        if (_fmtCtx == null) return;
        _clock.BeginSeek(position.TotalSeconds);
        RequestAnchor(position.TotalSeconds);
        ReleaseHeldFrame();
        _demuxThread?.RequestSeek(position.TotalSeconds);
        _playbackEndedFired = false;

        // 一時停止中のシークは、着地後の最初のフレームを即座に1枚だけ表示する
        if (_state == CorePlaybackState.Paused)
            TryHoldNextFrame(TimeSpan.FromMilliseconds(500));
    }

    /// <summary>次に実音声が mixer へ書かれた瞬間、その書込カーソル位置を srcPts=target としてクロックを起点合わせする。</summary>
    private void RequestAnchor(double targetSeconds)
    {
        _pendingAnchorTarget = targetSeconds;
        Interlocked.Exchange(ref _awaitingAnchor, 1);
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
        TryHoldNextFrame(TimeSpan.FromMilliseconds(500));
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
        // Paused 中に保持しているフレームは Step/Seek/Play で明示的に入れ替えるまで手放さない
        if (_state == CorePlaybackState.Playing)
            _videoRing?.ReturnLease(lease.SlotIndex);
    }

    private void ReleaseHeldFrame()
    {
        if (_heldLease is { } held)
            _videoRing?.ReturnLease(held.SlotIndex);
        _heldLease = null;
        _heldFrameConsumed = true;
    }

    private void TryHoldNextFrame(TimeSpan timeout)
    {
        if (_videoRing == null) return;
        if (!_videoRing.TryLeaseOldest(timeout, out var raw)) return;

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

    private void StatusTick()
    {
        if (_state == CorePlaybackState.Playing || _state == CorePlaybackState.Paused)
            PositionChanged?.Invoke(this, Position);

        StatisticsUpdated?.Invoke(this, new PlaybackStatistics(_droppedFrames, _displayedFrames, _lastVideoLagSec));
        CheckPlaybackEnded();
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
