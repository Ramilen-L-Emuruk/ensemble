using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

public sealed class MultiTrackMixerHoldOutputTests
{
    // 48kHz stereo float: 1 フレーム = 8 バイト
    private const int BlockAlign = 8;

    private static AudioTrackState CreateTrackWithSamples(int frameCount)
    {
        var track = new AudioTrackState();
        track.Buffer.AddSamples(new byte[frameCount * BlockAlign], 0, frameCount * BlockAlign);
        return track;
    }

    [Fact]
    public void Read_ReturnsSilence_AndDoesNotDrainBuffer_WhileHoldOutputIsTrue()
    {
        // Arrange: シーク後、映像プリロール完了待ちで音声出力を保留している状況を模す
        var mixer = new MultiTrackMixer();
        var track = CreateTrackWithSamples(1000);
        mixer.AddTrack(track);
        mixer.HoldOutput = true;

        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;

        var buffer = new byte[400 * BlockAlign];

        // Act
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: 無音のみが返り、トラックバッファは一切消費されない（裏で埋まり続けられる）
        Assert.Equal(0, audioFramesWritten);
        Assert.Equal(400, silenceFramesWritten);
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(1000 * BlockAlign, track.Buffer.BufferedBytes);
    }

    [Fact]
    public void Read_ResumesMixingRealAudio_AfterHoldOutputReleased()
    {
        // Arrange
        var mixer = new MultiTrackMixer();
        var track = CreateTrackWithSamples(1000);
        mixer.AddTrack(track);
        mixer.HoldOutput = true;

        long audioFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = _ => { };

        var buffer = new byte[400 * BlockAlign];
        mixer.Read(buffer, 0, buffer.Length); // 保留中: 消費なし

        // Act: 音声・映像双方のプリロール完了を模して解放
        mixer.HoldOutput = false;
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: 解放後は通常通りバッファから実データを消費する
        Assert.Equal(400, audioFramesWritten);
        Assert.Equal((1000 - 400) * BlockAlign, track.Buffer.BufferedBytes);
    }
}
