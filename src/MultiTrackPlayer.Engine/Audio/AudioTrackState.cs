using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class AudioTrackState
{
    public BufferedWaveProvider Buffer { get; }
    public volatile float Volume = 1.0f;
    public volatile bool IsMuted = false;
    // AudioDecodeThread が EOF ドレイン完了時に立てる。MediaEngine の再生完了検出（CheckPlaybackEnded）と、
    // MultiTrackMixer が全トラックEOF後の末尾無音でクロックを凍結させないための判定に使用する
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
            // 溢れさせないための充填ゲートは呼び出し側（現状 MediaEngine.DecodeLoop の1秒しきい値）が担う。
            DiscardOnBufferOverflow = false
        };
    }
}
