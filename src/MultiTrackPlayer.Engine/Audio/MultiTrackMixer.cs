using System.Linq;
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
    /// true の間、トラックバッファに実データがあっても Read() は無音を返す。バッファ自体は
    /// 通常どおり消費し続ける（消費まで止めると、AudioDecodeThread の充填ゲートが
    /// この Read() の消費待ちで永久に抜けられなくなり、シーク処理自体がデッドロックする）。
    /// シーク直後、映像側のプリロール（キーフレーム→目標地点の破棄デコード）が完了するまで音声出力を
    /// 保留するために使う。これが無いと音声だけ先に実時間で進んでクロックが映像を置き去りにし、
    /// 映像が追いつこうとして大量ドロップ（早送りに見える）が発生する。
    /// </summary>
    public volatile bool HoldOutput;

    private long _lastHoldOutputLogTicks;

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

        bool holding = HoldOutput;
        int common = ComputeCommonAvailableBytes(count);
        if (holding) LogHoldOutputStall(common);

        if (common > 0)
            MixCommonBytes(common, outFloats, holding);

        long audioFrames = holding ? 0 : common / _blockAlign;
        long silenceFrames = holding ? count / _blockAlign : (count - common) / _blockAlign;
        if (audioFrames > 0) OnAudioWritten?.Invoke(audioFrames);
        if (silenceFrames > 0) OnSilenceWritten?.Invoke(silenceFrames);

        OnRead?.Invoke();
        return count;
    }

    /// <summary>回帰検知用診断ログ: HoldOutput が長時間解除されない異常ケースを検出するため、
    /// HoldOutput 中の消費量とトラックのバッファ残量を一定間隔で記録する。</summary>
    private void LogHoldOutputStall(int consumedBytes)
    {
        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastHoldOutputLogTicks < 2000) return;
        _lastHoldOutputLogTicks = nowTicks;

        string bufferedByTrack = string.Join(",", _tracks.Select(t => t.Buffer.BufferedBytes));
        Diagnostics.DiagnosticLog.Write("mixer-hold",
            $"HoldOutput 中 出力保留（バッファ消費は継続） consumedBytes={consumedBytes} trackBufferedBytes=[{bufferedByTrack}]");
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

    /// <summary>
    /// holding=true の間はトラックバッファの消費（Read）だけ行い、出力には混ぜない。
    /// HoldOutput 中でも消費自体は止めないことで、AudioDecodeThread の充填ゲートが
    /// 塞がれ続けるのを防ぐ（HoldOutput のコメント参照）。
    /// </summary>
    private void MixCommonBytes(int common, Span<float> outFloats, bool holding)
    {
        EnsureScratchCapacity(common);
        var scratchBytes = _scratch;

        foreach (var track in _tracks)
        {
            int read = track.Buffer.Read(scratchBytes, 0, common);
            if (holding || track.IsMuted) continue;

            float vol = track.Volume * _masterVolume;
            if (vol == 0f) continue;

            var srcFloats = MemoryMarshal.Cast<byte, float>(scratchBytes.AsSpan(0, read));
            for (int i = 0; i < srcFloats.Length; i++)
                outFloats[i] += srcFloats[i] * vol;
        }

        if (holding) return; // 出力には混ぜない（消費だけ行った）

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
