using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class AudioTrackState
{
    public BufferedWaveProvider Buffer { get; }
    public volatile float Volume = 1.0f;
    public volatile bool IsMuted = false;
    // AudioDecodeThread が立てる（EOF ドレイン完了時、およびリサンプルできないトラックを切り離すとき）。
    // MediaEngine の再生完了検出（CheckPlaybackEnded）と、MultiTrackMixer が末尾無音でクロックを
    // 凍結させないための判定・共通利用可能量の計算からの除外に使用する
    public volatile bool IsEof = false;

    public AudioTrackState()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(
            Decoding.AudioDecoder.OutSampleRate,
            Decoding.AudioDecoder.OutChannels);
        Buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            // discard は無音混入なしにトラック間位相を壊す（あるトラックだけ静かに捨てられると他トラックとズレる）。
            // 溢れさせないための充填ゲートは呼び出し側（AudioDecodeThread.FillGateThreshold の 1 秒しきい値）が担う。
            DiscardOnBufferOverflow = false
        };
    }
}
