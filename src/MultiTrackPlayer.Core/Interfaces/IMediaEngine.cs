using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Core.Interfaces;

public interface IMediaEngine : IDisposable
{
    MediaInfo? CurrentMedia { get; }
    PlaybackState State { get; }
    TimeSpan Position { get; }
    double PlaybackSpeed { get; }

    void Open(string filePath);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetPlaybackSpeed(double speed);
    void StepForward();
    void StepBackward();
    void SetTrackVolume(int trackNumber, float volume);
    void SetTrackMute(int trackNumber, bool muted);
    void SetMasterVolume(float volume);

    IReadOnlyList<ChapterInfo> GetChapters();
    void JumpToChapter(int index);
    void JumpToPreviousChapter();
    void JumpToNextChapter();

    event EventHandler<VideoFrameData> VideoFrameReady;
    event EventHandler<TimeSpan> PositionChanged;
    event EventHandler PlaybackEnded;
    event EventHandler<PlaybackStatistics>? StatisticsUpdated;
}

public record PlaybackStatistics(int DroppedFrames, int DisplayedFrames, double AverageDriftSec);
