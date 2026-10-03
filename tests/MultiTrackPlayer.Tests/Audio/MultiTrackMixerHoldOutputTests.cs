using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

public sealed class MultiTrackMixerHoldOutputTests
{
    // 48kHz stereo float: 1 フレーム = 8 バイト
    private const int BlockAlign = 8;
    private const float Scale = 1e-6f;

    private static readonly long GraceMs = (long)MultiTrackMixer.HoldDiscardGrace.TotalMilliseconds;

    /// <summary>
    /// フレーム番号 i の左右チャンネルに値 i×<see cref="Scale"/> を入れる（どこから読んだかを後で確かめるため。
    /// 合成は ±1 でクランプされるので、値を小さく縮めておく）。
    /// </summary>
    private static byte[] NumberedFrames(int frameCount)
    {
        var floats = new float[frameCount * 2];
        for (int i = 0; i < frameCount; i++)
        {
            floats[2 * i] = i * Scale;
            floats[2 * i + 1] = i * Scale;
        }
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static AudioTrackState CreateTrackWithFrames(int frameCount)
    {
        var track = new AudioTrackState();
        var pcm = NumberedFrames(frameCount);
        track.Buffer.AddSamples(pcm, 0, pcm.Length);
        return track;
    }

    private static float FirstLeftSample(byte[] buffer) => BitConverter.ToSingle(buffer, 0);

    /// <summary>テストが時刻を進められるミキサー。</summary>
    private sealed class Harness
    {
        public long NowMs;
        public long AudioFrames;
        public long SilenceFrames;
        public readonly MultiTrackMixer Mixer;

        public Harness()
        {
            Mixer = new MultiTrackMixer(() => NowMs);
            Mixer.OnAudioWritten = f => AudioFrames += f;
            Mixer.OnSilenceWritten = f => SilenceFrames += f;
        }
    }

    [Fact(DisplayName = "保留中は無音を出し、バッファは読み進めない")]
    public void Read_ReturnsSilence_AndKeepsBuffer_WhileHoldOutputIsTrue()
    {
        // Arrange: シーク後、映像プリロール完了待ちで音声出力を保留している状況を模す
        var h = new Harness();
        var track = CreateTrackWithFrames(1000);
        h.Mixer.AddTrack(track);
        h.Mixer.HoldOutput = true;

        var buffer = new byte[400 * BlockAlign];

        // Act
        h.Mixer.Read(buffer, 0, buffer.Length);

        // Assert: 出力は無音。バッファの中身（＝シーク先からの音声）は解除まで手を付けない。
        // 読み進めると、その分だけ解除後の音声が先から始まり、音声が映像より先行したまま残る
        Assert.Equal(0, h.AudioFrames);
        Assert.Equal(400, h.SilenceFrames);
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(1000 * BlockAlign, track.Buffer.BufferedBytes);
    }

    [Fact(DisplayName = "保留が解けたら、溜めておいた先頭（シーク先）から鳴らす")]
    public void Read_AfterHoldOutputReleased_StartsFromBufferHead()
    {
        // Arrange: 猶予の内側で何度か読まれる
        var h = new Harness();
        var track = CreateTrackWithFrames(1000);
        h.Mixer.AddTrack(track);
        h.Mixer.HoldOutput = true;

        var buffer = new byte[400 * BlockAlign];
        h.Mixer.Read(buffer, 0, buffer.Length);
        h.NowMs = GraceMs - 1;
        h.Mixer.Read(buffer, 0, buffer.Length);

        // Act: 音声・映像双方のプリロール完了を模して解放
        h.Mixer.HoldOutput = false;
        h.Mixer.Read(buffer, 0, buffer.Length);

        // Assert: 最初に鳴るのはフレーム 0（保留中に捨てていない）
        Assert.Equal(400, h.AudioFrames);
        Assert.Equal(0f, FirstLeftSample(buffer));
        Assert.Equal((1000 - 400) * BlockAlign, track.Buffer.BufferedBytes);
    }

    [Fact(DisplayName = "保留が猶予を超えたら、読み進めを再開する（デコードを止め続けないため）")]
    public void Read_WhenHoldOutlastsGrace_ResumesDrainingBuffer()
    {
        // Arrange
        var h = new Harness();
        var trackA = CreateTrackWithFrames(1000);
        var trackB = CreateTrackWithFrames(1000);
        h.Mixer.AddTrack(trackA);
        h.Mixer.AddTrack(trackB);
        h.Mixer.HoldOutput = true;

        var buffer = new byte[400 * BlockAlign];

        // Act: 猶予ちょうどに達した
        h.NowMs = GraceMs;
        h.Mixer.Read(buffer, 0, buffer.Length);

        // Assert: 出力は無音のまま、両トラックから同量を読み進める（位相を保つ）。クロックは進めない
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(0, h.AudioFrames);
        Assert.Equal(400, h.SilenceFrames);
        Assert.Equal((1000 - 400) * BlockAlign, trackA.Buffer.BufferedBytes);
        Assert.Equal((1000 - 400) * BlockAlign, trackB.Buffer.BufferedBytes);
    }

    [Fact(DisplayName = "保留を立て直すたびに猶予の計時をやり直す（シークの連打で猶予を使い切らない）")]
    public void HoldOutput_SetTrueAgain_RestartsGrace()
    {
        // Arrange: 猶予の直前まで保留したところで、次のシークが保留を立て直す
        var h = new Harness();
        var track = CreateTrackWithFrames(1000);
        h.Mixer.AddTrack(track);
        h.Mixer.HoldOutput = true;
        h.NowMs = GraceMs - 1;
        h.Mixer.HoldOutput = true;

        // Act: 最初の保留から数えれば猶予を超えているが、立て直しからはまだ内側
        h.NowMs = GraceMs + GraceMs / 2;
        h.Mixer.Read(new byte[400 * BlockAlign], 0, 400 * BlockAlign);

        // Assert
        Assert.Equal(1000 * BlockAlign, track.Buffer.BufferedBytes);
    }
}
