using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Core.Interfaces;

public interface IMediaEngine : IDisposable
{
    MediaInfo? CurrentMedia { get; }

    /// <summary>現在の再生状態。表示側はこの値を唯一の情報源とし、独自に状態を持たないこと。</summary>
    PlaybackState State { get; }

    /// <summary>
    /// 再生状態が変化したときに発火する。UI スレッド以外からも発火するため、
    /// 購読側でディスパッチャへ移すこと。
    /// </summary>
    event EventHandler<PlaybackState> StateChanged;

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

    /// <summary>
    /// 現在位置に表示すべき新しいフレームがあればリースして返す（無ければ null）。
    /// 呼び出し側は使い終えたら必ず ReturnFrame で返却すること。呼び出しは UI スレッドから行う想定。
    /// </summary>
    VideoFrameLease? TryGetFrame(TimeSpan position);
    void ReturnFrame(VideoFrameLease lease);

    event EventHandler<TimeSpan> PositionChanged;
    event EventHandler PlaybackEnded;
    event EventHandler<PlaybackStatistics>? StatisticsUpdated;
}

public record PlaybackStatistics(int DroppedFrames, int DisplayedFrames, double VideoLagSec);
