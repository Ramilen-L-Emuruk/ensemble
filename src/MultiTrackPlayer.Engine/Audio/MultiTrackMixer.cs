using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class MultiTrackMixer : IWaveProvider
{
    private readonly List<AudioTrackState> _tracks = new();
    private readonly WaveFormat _format;
    private readonly int _blockAlign;
    private float _masterVolume = 1.0f;
    private byte[] _scratch = Array.Empty<byte>();

    public WaveFormat WaveFormat => _format;

    /// <summary>実際に混合された音声のフレーム数（Read 完了ごと）。PlaybackClock.OnAudioWritten に配線する。</summary>
    public Action<long>? OnAudioWritten;
    /// <summary>無音で埋めたフレーム数（アンダーラン/バッファ待ち等）。PlaybackClock.OnSilenceWritten に配線する。</summary>
    public Action<long>? OnSilenceWritten;
    /// <summary>Read() 完了ごとに呼ばれる。AudioDecodeThread の充填ゲート待ちを起こすためのフック。</summary>
    public Action? OnRead;

    /// <summary>
    /// true の間、トラックバッファに実データがあっても Read() は無音を返す（バッファ自体は裏で埋まり続ける）。
    /// シーク直後、映像側のプリロール（キーフレーム→目標地点の破棄デコード）が完了するまで音声出力を
    /// 保留するために使う。これが無いと音声だけ先に実時間で進んでクロックが映像を置き去りにし、
    /// 映像が追いつこうとして大量ドロップ（早送りに見える）が発生する。
    /// </summary>
    public volatile bool HoldOutput;

    public MultiTrackMixer()
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(
            Decoding.AudioDecoder.OutSampleRate,
            Decoding.AudioDecoder.OutChannels);
        _blockAlign = _format.BlockAlign;
    }

    public void AddTrack(AudioTrackState track) => _tracks.Add(track);
    public void RemoveAllTracks() => _tracks.Clear();
    public void SetMasterVolume(float v) => _masterVolume = Math.Clamp(v, 0f, 1f);

    public int Read(byte[] buffer, int offset, int count)
    {
        var outFloats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        outFloats.Clear();

        int common = HoldOutput ? 0 : ComputeCommonAvailableBytes(count);
        if (common > 0)
            MixCommonBytes(common, outFloats);

        long audioFrames = common / _blockAlign;
        long silenceFrames = (count - common) / _blockAlign;
        if (audioFrames > 0) OnAudioWritten?.Invoke(audioFrames);
        if (silenceFrames > 0) OnSilenceWritten?.Invoke(silenceFrames);

        OnRead?.Invoke();
        return count;
    }

    /// <summary>
    /// 全トラック共通の読める量だけをミックスする。トラックごとに独立して読むと、
    /// あるトラックだけアンダーラン/discard したときにトラック間の位相がずれるため、
    /// 常に全トラックから同量を消費してから合成する（ミュートトラックも消費だけは行う）。
    /// </summary>
    private int ComputeCommonAvailableBytes(int count)
    {
        if (_tracks.Count == 0) return 0;

        // EOF 済みトラックは可用量の下限計算から除外する（残量ゼロの EOF トラックが
        // 常に common=0 を強制し、他トラックの音声まで止めてしまうのを防ぐ）。
        // Read 自体は全トラックに対して行うので、EOF トラックの残りも消費される
        int common = int.MaxValue;
        foreach (var track in _tracks)
        {
            if (track.IsEof) continue;
            common = Math.Min(common, track.Buffer.BufferedBytes);
        }
        if (common == int.MaxValue) common = 0; // 全トラック EOF

        common = Math.Min(common, count);
        common -= common % _blockAlign;
        return Math.Max(0, common);
    }

    private void MixCommonBytes(int common, Span<float> outFloats)
    {
        EnsureScratchCapacity(common);
        var scratchBytes = _scratch;

        foreach (var track in _tracks)
        {
            int read = track.Buffer.Read(scratchBytes, 0, common);
            if (track.IsMuted) continue;

            float vol = track.Volume * _masterVolume;
            if (vol == 0f) continue;

            var srcFloats = MemoryMarshal.Cast<byte, float>(scratchBytes.AsSpan(0, read));
            for (int i = 0; i < srcFloats.Length; i++)
                outFloats[i] += srcFloats[i] * vol;
        }

        int floatCount = common / sizeof(float);
        for (int i = 0; i < floatCount; i++)
            outFloats[i] = Math.Clamp(outFloats[i], -1f, 1f);
    }

    private void EnsureScratchCapacity(int size)
    {
        if (_scratch.Length < size)
            _scratch = new byte[size];
    }
}
