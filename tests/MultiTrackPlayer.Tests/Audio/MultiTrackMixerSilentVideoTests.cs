using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

/// <summary>
/// 音声トラックを 1 本も持たない動画（無音動画）で再生が固まらないことを守る。
/// OnAudioWritten が一度も発火しないと PlaybackClock が 0.0 に固定され、2 枚目以降の
/// 映像フレームが永久に「期限到来」と判定されず最初のフレームで止まる（GPU/CPU 両経路）。
/// </summary>
public sealed class MultiTrackMixerSilentVideoTests
{
    // 48kHz stereo float: 1 フレーム = 8 バイト
    private const int BlockAlign = 8;

    [Fact(DisplayName = "音声トラックが 0 本のとき無音を実時間として計上する")]
    public void Read_TreatsSilenceAsRealAudio_WhenNoAudioTracks()
    {
        // Arrange: 音声ストリームを持たない動画を開いた状態（トラックを 1 本も追加しない）
        var mixer = new MultiTrackMixer();
        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;
        var buffer = new byte[400 * BlockAlign];

        // Act
        int read = mixer.Read(buffer, 0, buffer.Length);

        // Assert: このトラック構成では実データが来ることは二度とないため、クロックを凍結させる
        // OnSilenceWritten ではなく OnAudioWritten に計上されなければならない
        Assert.Equal(buffer.Length, read);
        Assert.Equal(400, audioFramesWritten);
        Assert.Equal(0, silenceFramesWritten);
    }

    [Fact(DisplayName = "音声トラックが 0 本でも出力保留中はクロックを進めない")]
    public void Read_KeepsClockFrozen_WhenNoAudioTracksAndOutputHeld()
    {
        // Arrange: 無音動画のシーク直後（映像側のプリロール完了まで出力を保留している状態）
        var mixer = new MultiTrackMixer { HoldOutput = true };
        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;
        var buffer = new byte[400 * BlockAlign];

        // Act
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: 保留中の無音を実時間として計上すると、映像を置き去りにしてクロックだけ進む
        Assert.Equal(0, audioFramesWritten);
        Assert.Equal(400, silenceFramesWritten);
    }
}
