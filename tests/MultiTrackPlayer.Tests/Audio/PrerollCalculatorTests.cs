using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Audio;

public sealed class PrerollCalculatorTests
{
    [Fact]
    public void ComputeAction_ReturnsDrop_WhenFrameEntirelyBeforeTarget()
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.0, nbSamples: 1024, inSampleRate: 48000,
            targetPtsSeconds: 1.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.DropAll, action.Kind);
    }

    [Fact]
    public void ComputeAction_ReturnsKeep_WhenFrameEntirelyAtOrAfterTarget()
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 1.0, nbSamples: 1024, inSampleRate: 48000,
            targetPtsSeconds: 0.5, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.KeepAll, action.Kind);
    }

    [Fact]
    public void ComputeAction_ReturnsKeep_WhenFrameStartExactlyEqualsTarget()
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 2.0, nbSamples: 1024, inSampleRate: 48000,
            targetPtsSeconds: 2.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.KeepAll, action.Kind);
    }

    [Fact]
    public void ComputeAction_ReturnsDrop_WhenFrameEndExactlyEqualsTarget()
    {
        // frameEnd = 1.0 + 48000/48000 = 2.0 = target
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 1.0, nbSamples: 48000, inSampleRate: 48000,
            targetPtsSeconds: 2.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.DropAll, action.Kind);
    }

    [Fact]
    public void ComputeAction_ReturnsSkipBytes_WhenFrameStraddlesTarget_AtNormalSpeed()
    {
        // frame: [0.9, 1.9), target=1.0 → skip 0.1秒
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.9, nbSamples: 48000, inSampleRate: 48000,
            targetPtsSeconds: 1.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.SkipBytes, action.Kind);
        // 0.1s * 48000 = 4800 samples * 2ch * 4bytes = 38400
        Assert.Equal(38400, action.SkipByteCount);
    }

    [Fact]
    public void ComputeAction_ScalesSkipBytes_WithEffectiveOutSampleRate_ForSpeedChange()
    {
        // 同じ跨ぎだが 2x 速再生中（effectiveOutRate = 24000）
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.9, nbSamples: 48000, inSampleRate: 48000,
            targetPtsSeconds: 1.0, effectiveOutSampleRate: 24000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.SkipBytes, action.Kind);
        // 0.1s * 24000 = 2400 samples * 2ch * 4bytes = 19200
        Assert.Equal(19200, action.SkipByteCount);
    }

    [Fact]
    public void ComputeAction_HandlesMonoAndDifferentSampleWidth()
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.5, nbSamples: 48000, inSampleRate: 48000,
            targetPtsSeconds: 0.75, effectiveOutSampleRate: 48000, channels: 1, bytesPerSample: 2);

        Assert.Equal(PrerollActionKind.SkipBytes, action.Kind);
        // 0.25s * 48000 = 12000 samples * 1ch * 2bytes = 24000
        Assert.Equal(24000, action.SkipByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeAction_ReturnsKeep_WhenNbSamplesIsZeroOrNegative(int nbSamples)
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.0, nbSamples: nbSamples, inSampleRate: 48000,
            targetPtsSeconds: 1.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.KeepAll, action.Kind);
    }

    [Fact]
    public void ComputeAction_ReturnsKeep_WhenInSampleRateIsZero()
    {
        var action = PrerollCalculator.ComputeAction(
            frameStartPtsSeconds: 0.0, nbSamples: 1024, inSampleRate: 0,
            targetPtsSeconds: 1.0, effectiveOutSampleRate: 48000, channels: 2, bytesPerSample: 4);

        Assert.Equal(PrerollActionKind.KeepAll, action.Kind);
    }
}
