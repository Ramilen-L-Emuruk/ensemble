using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

public sealed class MultiTrackMixerEofTests
{
    // 48kHz stereo float: 1 フレーム = 8 バイト
    private const int BlockAlign = 8;

    [Fact]
    public void Read_TreatsTrailingSilenceAsRealAudio_WhenAllTracksReachedEof()
    {
        // Arrange: 全トラックがバッファを使い切った上で EOF に達した状態（動画の再生完了直前）を模す
        var mixer = new MultiTrackMixer();
        var track = new AudioTrackState { IsEof = true };
        mixer.AddTrack(track);

        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;

        var buffer = new byte[400 * BlockAlign];

        // Act
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: 無音区間だが「アンダーラン」ではなく「本当に終わった」ので、
        // クロックを凍結させる OnSilenceWritten ではなく OnAudioWritten に計上されるべき
        // （凍結させると音声より僅かに長い映像側の末尾フレームが永遠に期限到来判定されず、
        // 再生完了検出が働かなくなる）
        Assert.Equal(400, audioFramesWritten);
        Assert.Equal(0, silenceFramesWritten);
    }

    [Fact]
    public void Read_DrainsRemainingBufferedBytes_WhenTrackIsEofButStillHasData()
    {
        // Arrange: 全トラックがEOFに達した直後、まだ末尾の未消費データが残っている状況を模す
        var mixer = new MultiTrackMixer();
        var track = new AudioTrackState { IsEof = true };
        track.Buffer.AddSamples(new byte[200 * BlockAlign], 0, 200 * BlockAlign);
        mixer.AddTrack(track);

        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;

        var buffer = new byte[400 * BlockAlign];

        // Act: 400フレーム分読もうとするが、残っているのは200フレーム分だけ
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: 残っていた200フレームはきちんと消費されバッファが空になる（このバイトが
        // 消費されないまま残ると CheckPlaybackEnded の BufferedBytes==0 判定が永久に成立しない）。
        // 消費した分・埋めた無音分とも、全トラックEOFなのでクロックは凍結させず実時間で進める
        Assert.Equal(0, track.Buffer.BufferedBytes);
        Assert.Equal(400, audioFramesWritten);
        Assert.Equal(0, silenceFramesWritten);
    }

    [Fact]
    public void Read_TreatsSilenceAsUnderrun_WhenSomeTrackNotYetEof()
    {
        // Arrange: 1トラックだけEOF、もう1トラックはまだ再生中だがバッファが枯渇（アンダーラン）
        var mixer = new MultiTrackMixer();
        var eofTrack = new AudioTrackState { IsEof = true };
        var underrunTrack = new AudioTrackState(); // IsEof = false, バッファ空
        mixer.AddTrack(eofTrack);
        mixer.AddTrack(underrunTrack);

        long audioFramesWritten = 0, silenceFramesWritten = 0;
        mixer.OnAudioWritten = f => audioFramesWritten += f;
        mixer.OnSilenceWritten = f => silenceFramesWritten += f;

        var buffer = new byte[400 * BlockAlign];

        // Act
        mixer.Read(buffer, 0, buffer.Length);

        // Assert: まだ実データが来る可能性があるアンダーランなので、従来通りクロックを凍結すべき
        Assert.Equal(0, audioFramesWritten);
        Assert.Equal(400, silenceFramesWritten);
    }
}
