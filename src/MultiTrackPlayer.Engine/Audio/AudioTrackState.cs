using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class AudioTrackState
{
    public BufferedWaveProvider Buffer { get; }
    public volatile float Volume = 1.0f;
    public volatile bool IsMuted = false;

    public AudioTrackState()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(
            Decoding.AudioDecoder.OutSampleRate,
            Decoding.AudioDecoder.OutChannels);
        Buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
    }
}
