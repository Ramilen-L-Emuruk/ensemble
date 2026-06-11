using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Core.Models;

public class MediaInfo
{
    public string FilePath { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasHdr { get; init; }
    public int VideoStreamIndex { get; init; }
    public IReadOnlyList<AudioTrackInfo> AudioTracks { get; init; } = Array.Empty<AudioTrackInfo>();
    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = Array.Empty<ChapterInfo>();
}
