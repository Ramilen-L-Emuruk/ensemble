using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Interfaces;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
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
    private MultiTrackMixer? _mixer;
    private WasapiOut? _wasapiOut;

    private Thread? _decodeThread;
    private CancellationTokenSource? _cts;
    private readonly object _seekLock = new();

    private MediaInfo? _currentMedia;
    private CorePlaybackState _state = CorePlaybackState.Stopped;
    private double _playbackSpeed = 1.0;
    private List<ChapterInfo> _chapters = new();
    // 最初のビデオフレームのPTSを基準として正規化（OBS Replay Bufferなど大きなPTSに対応）
    private double _ptsSyncOffset = double.NaN;
    // WASAPI 出力バッファ遅延（秒）: Init() 後に取得し masterClock から引いて実出力タイミングに補正
    private double _wasapiLatencySec;
    // 映像の1フレーム時間（秒）: フレームドロップ閾値の動的計算に使用
    private double _videoFrameDuration = 1.0 / 30.0;

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

    public void Open(string filePath)
    {
        Stop();
        DisposeDecoders();
        _ptsSyncOffset = double.NaN;

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
        // WasapiOut の要求レイテンシ（デフォルト 200ms）を記録し、masterClock 補正に使う
        const int WasapiLatencyMs = 200;
        _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, WasapiLatencyMs);
        _wasapiOut.Init(_mixer);
        _wasapiLatencySec = WasapiLatencyMs / 1000.0;
    }

    public void Play()
    {
        if (_fmtCtx == null) return;
        if (_state == CorePlaybackState.Playing) return;
        _state = CorePlaybackState.Playing;
        _wasapiOut?.Play();
        if (_decodeThread == null || !_decodeThread.IsAlive)
        {
            _cts = new CancellationTokenSource();
            _decodeThread = new Thread(() => DecodeLoop(_cts.Token)) { IsBackground = true };
            _decodeThread.Start();
        }
    }

    public void Pause()
    {
        if (_state != CorePlaybackState.Playing) return;
        _state = CorePlaybackState.Paused;
        _wasapiOut?.Pause();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _state = CorePlaybackState.Stopped;
        _wasapiOut?.Stop();
        foreach (var s in _audioStates) s.Buffer.ClearBuffer();
    }

    public void Seek(TimeSpan position)
    {
        if (_fmtCtx == null) return;
        lock (_seekLock)
        {
            long ts = (long)(position.TotalSeconds * AV_TIME_BASE);
            av_seek_frame(_fmtCtx, -1, ts, (int)AVSEEK_FLAG.Backward);
            _videoDecoder?.FlushBuffers();
            foreach (var d in _audioDecoders) d.FlushBuffers();
            foreach (var s in _audioStates) s.Buffer.ClearBuffer();
            _mixer?.SetPlayedSamples((long)(position.TotalSeconds * AudioDecoder.OutSampleRate));
            // シーク後もPTSオフセットは保持（最初のフレームで確定した相対オフセットは変わらない）
        }
    }

    public void SetPlaybackSpeed(double speed) => _playbackSpeed = Math.Clamp(speed, 0.1, 2.0);

    public void StepForward()
    {
        if (_state != CorePlaybackState.Paused) return;
        var frame = DecodeOneVideoFrame();
        if (frame != null) VideoFrameReady?.Invoke(this, frame);
    }

    public void StepBackward()
    {
        if (_state != CorePlaybackState.Paused) return;
        var target = Position - TimeSpan.FromMilliseconds(40);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        Seek(target);
        var frame = DecodeOneVideoFrame();
        if (frame != null) VideoFrameReady?.Invoke(this, frame);
    }

    private VideoFrameData? DecodeOneVideoFrame()
    {
        if (_fmtCtx == null || _videoDecoder == null) return null;
        using var pkt = new PacketHolder();
        while (av_read_frame(_fmtCtx, pkt.Packet) >= 0)
        {
            if (pkt.Packet->stream_index == _videoDecoder.StreamIndex)
            {
                var frame = _videoDecoder.DecodePacket(pkt.Packet);
                av_packet_unref(pkt.Packet);
                if (frame != null) return frame;
            }
            av_packet_unref(pkt.Packet);
        }
        return null;
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

    private void DecodeLoop(CancellationToken ct)
    {
        if (_fmtCtx == null) return;
        using var pkt = new PacketHolder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_state == CorePlaybackState.Paused) { Thread.Sleep(10); continue; }

                bool bufferFull = _audioStates.Count > 0 &&
                                  _audioStates.Any(s => s.Buffer.BufferedDuration > TimeSpan.FromSeconds(1));
                if (bufferFull) { Thread.Sleep(5); continue; }

                lock (_seekLock) { }

                int ret = av_read_frame(_fmtCtx, pkt.Packet);
                if (ret < 0)
                {
                    Thread.Sleep(200);
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                    return;
                }

                int idx = pkt.Packet->stream_index;

                if (_videoDecoder != null && idx == _videoDecoder.StreamIndex)
                {
                    var frame = _videoDecoder.DecodePacket(pkt.Packet);
                    if (frame != null)
                    {
                        double absFramePts = frame.Pts.TotalSeconds;

                        // 初回フレームでPTSオフセットを確定（OBS Replay Bufferなど非ゼロ開始PTSに対応）
                        if (double.IsNaN(_ptsSyncOffset))
                            _ptsSyncOffset = absFramePts;

                        double normalizedPts = absFramePts - _ptsSyncOffset;
                        // PlayedSamples はWASAPIバッファへの書き込み数のため、実際の出力は _wasapiLatencySec 分遅れる
                        double masterClock = (_mixer?.PlayedSamples ?? 0) / (double)AudioDecoder.OutSampleRate
                                            - _wasapiLatencySec;
                        double diff = normalizedPts - masterClock;

                        // 映像が音声より先行している場合は待つ（最大100ms）
                        if (diff > 0.002)
                            Thread.Sleep((int)(Math.Min(diff, 0.1) * 1000));

                        // 映像が2フレーム以上遅れている場合はスキップして追いつかせる（VLC の VOUT_DISPLAY_LATE_THRESHOLD 相当）
                        if (diff >= -_videoFrameDuration * 2)
                        {
                            VideoFrameReady?.Invoke(this, frame);
                            PositionChanged?.Invoke(this, TimeSpan.FromSeconds(normalizedPts));
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < _audioDecoders.Count; i++)
                    {
                        if (_audioDecoders[i].StreamIndex == idx)
                        {
                            var pcm = _audioDecoders[i].DecodePacket(pkt.Packet);
                            if (pcm != null)
                                _audioStates[i].Buffer.AddSamples(pcm, 0, pcm.Length);
                            break;
                        }
                    }
                }

                av_packet_unref(pkt.Packet);
            }
        }
        catch (Exception)
        {
            // デコードスレッドの例外でアプリがクラッシュしないよう捕捉
            // （D3D11VAの失敗、ネイティブクラッシュなどへの安全網）
        }
        finally
        {
            _decodeThread = null;
        }
    }

    private void DisposeDecoders()
    {
        _videoDecoder?.Dispose(); _videoDecoder = null;
        foreach (var d in _audioDecoders) d.Dispose();
        _audioDecoders.Clear();
        _audioStates.Clear();
        _wasapiOut?.Dispose(); _wasapiOut = null;
        _mixer = null;
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) avformat_close_input(p); }
        _fmtCtx = null;
    }

    public void Dispose() { Stop(); DisposeDecoders(); }

    private unsafe class PacketHolder : IDisposable
    {
        public AVPacket* Packet = av_packet_alloc();
        public void Dispose() { if (Packet != null) { AVPacket* p = Packet; av_packet_free(&p); } Packet = null; }
    }
}