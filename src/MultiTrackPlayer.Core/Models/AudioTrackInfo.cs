namespace MultiTrackPlayer.Core.Models;

public record AudioTrackInfo(
    int StreamIndex,
    int TrackNumber,
    string Name,
    string Language,
    string CodecName,
    int Channels,
    int SampleRate);
