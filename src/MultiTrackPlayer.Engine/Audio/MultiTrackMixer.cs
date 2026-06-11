using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class MultiTrackMixer : IWaveProvider
{
    private readonly List<AudioTrackState> _tracks = new();
    private readonly WaveFormat _format;
    private float _masterVolume = 1.0f;
    private long _playedSamples;

    public WaveFormat WaveFormat => _format;
    public long PlayedSamples => Interlocked.Read(ref _playedSamples);
    public void SetPlayedSamples(long value) => Interlocked.Exchange(ref _playedSamples, value);

    public MultiTrackMixer()
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(
            Decoding.AudioDecoder.OutSampleRate,
            Decoding.AudioDecoder.OutChannels);
    }

    public void AddTrack(AudioTrackState track) => _tracks.Add(track);
    public void RemoveAllTracks() => _tracks.Clear();
    public void SetMasterVolume(float v) => _masterVolume = Math.Clamp(v, 0f, 1f);

    public int Read(byte[] buffer, int offset, int count)
    {
        int floatCount = count / sizeof(float);
        var mixed = new float[floatCount];

        var tmp = new byte[count];
        foreach (var track in _tracks)
        {
            if (track.IsMuted) continue;
            float vol = track.Volume * _masterVolume;

            int read = track.Buffer.Read(tmp, 0, count);
            int readFloats = read / sizeof(float);
            for (int i = 0; i < readFloats; i++)
            {
                float sample = BitConverter.ToSingle(tmp, i * sizeof(float));
                mixed[i] += sample * vol;
            }
        }

        for (int i = 0; i < floatCount; i++)
            mixed[i] = Math.Clamp(mixed[i], -1f, 1f);

        Buffer.BlockCopy(mixed, 0, buffer, offset, count);

        // WASAPIが実際に再生したサンプル数をカウント（これがマスタークロック）
        Interlocked.Add(ref _playedSamples, count / sizeof(float) / _format.Channels);

        return count;
    }
}