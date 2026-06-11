namespace MultiTrackPlayer.Core.Models;

public record ChapterInfo(
    int Index,
    string Title,
    TimeSpan StartTime,
    bool IsUserDefined);
