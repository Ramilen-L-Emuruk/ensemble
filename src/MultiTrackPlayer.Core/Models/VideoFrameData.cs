namespace MultiTrackPlayer.Core.Models;

public record VideoFrameData(
    byte[] Pixels,
    int Width,
    int Height,
    TimeSpan Pts);
