using MultiTrackPlayer.Engine.Sync;

namespace MultiTrackPlayer.Tests.Sync;

public sealed class PlaybackClockTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void InitialState_PositionIsZero_AndFrozen()
    {
        var clock = new PlaybackClock(SampleRate);
        Assert.Equal(0.0, clock.PositionAt(0));
    }

    [Fact]
    public void OnSilenceWritten_AdvancesWriteCursor_ButNotPosition()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.OnSilenceWritten(SampleRate); // 1秒分の無音

        Assert.Equal(SampleRate, clock.WriteCursor);
        Assert.Equal(0.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void AnchorAt_Then_OnAudioWritten_AdvancesPositionAtRate1()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(atFrame: 0, srcPtsSeconds: 10.0);
        clock.OnAudioWritten(SampleRate); // 1秒分の実音声

        Assert.Equal(11.0, clock.PositionAt(SampleRate), precision: 6);
    }

    [Fact]
    public void BeginSeek_PositionReturnsTarget_WhilePending()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate);

        clock.BeginSeek(30.0);

        Assert.Equal(30.0, clock.PositionAt(clock.WriteCursor));
        Assert.Equal(30.0, clock.PositionAt(999999)); // hwFrames が何であっても保留中は target 固定
    }

    [Fact]
    public void AnchorAt_ResolvesSeek_AndPositionAdvancesFromTarget()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.BeginSeek(30.0);

        long anchorFrame = clock.WriteCursor;
        clock.AnchorAt(anchorFrame, 30.0);
        clock.OnAudioWritten(SampleRate);

        Assert.Equal(31.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void SetSpeedAt_AppliesNewRate_OnlyFromBoundaryFrame_WithNoDiscontinuity()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate); // 1.0 秒経過（等速）

        Assert.Equal(1.0, clock.PositionAt(clock.WriteCursor), precision: 6);

        long boundary = clock.WriteCursor; // ここから 2x を適用
        clock.SetSpeedAt(boundary, newRate: 2.0);

        // 境界直後は連続（ジャンプなし）
        Assert.Equal(1.0, clock.PositionAt(boundary), precision: 6);

        clock.OnAudioWritten(SampleRate); // 2x で 1 出力秒 = 2 ソース秒
        Assert.Equal(3.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void SetSpeedAt_WithFutureBoundary_DoesNotAffectPositionBeforeBoundary()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate / 2); // 0.5 秒経過（等速）

        long futureBoundary = clock.WriteCursor + SampleRate; // まだ到達していない境界
        clock.SetSpeedAt(futureBoundary, newRate: 2.0);

        // 境界前は旧レート(1.0)のまま
        Assert.Equal(0.5, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 5.0);
        clock.OnAudioWritten(SampleRate);
        clock.BeginSeek(99.0);

        clock.Reset();

        Assert.Equal(0, clock.WriteCursor);
        Assert.Equal(0.0, clock.PositionAt(0));
        Assert.Null(clock.PausedOverride);
    }

    [Fact]
    public void PausedOverride_TakesPrecedence_UntilCleared()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate);

        clock.PausedOverride = 5.0;
        Assert.Equal(5.0, clock.PositionAt(clock.WriteCursor));

        clock.PausedOverride = null;
        Assert.Equal(1.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void PositionAt_IsMonotonic_DespiteBackwardHwFrameJitter()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate); // writeCursor = 48000, position = 1.0

        Assert.Equal(1.0, clock.PositionAt(SampleRate), precision: 6);

        // QPC 外挿のジッタで一瞬 hwFrames が後退したケースを模す
        double jittered = clock.PositionAt(SampleRate - 1000);
        Assert.True(jittered >= 1.0, "後退したフレーム数を渡しても位置は単調非減少であるべき");
    }

    [Fact]
    public void BeginSeek_PurgesStaleFutureSpeedChangeSegment_SoLaterFramesDontRevertToOldTimeline()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate); // cursor=48000, position=1.0（等速）

        // 未来の境界(96000)に 2x 速度変更を予約（バッファ残量ぶん遅れて発効する想定）
        long staleFutureBoundary = clock.WriteCursor + SampleRate;
        clock.SetSpeedAt(staleFutureBoundary, newRate: 2.0);

        // 発効前にシークが割り込む。BeginSeek は現在カーソル(48000)以降の予約セグメントを破棄すべき
        clock.BeginSeek(30.0);
        Assert.Equal(30.0, clock.PositionAt(clock.WriteCursor));

        long anchorFrame = clock.WriteCursor; // 48000
        clock.AnchorAt(anchorFrame, 30.0);

        // 破棄された未来境界(96000)を跨いで書き進める。ここでその古いセグメントが生き残っていると
        // 位置計算がシーク前のタイムラインに巻き戻ってしまう。
        clock.OnAudioWritten(2 * SampleRate);

        // シーク後は 2x（_currentRate）で連続進行するはずなので 30.0 + 2.0s*2.0 = 34.0
        Assert.Equal(34.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact]
    public void BackwardSeek_AllowsPositionToDecrease_PastMonotonicClamp()
    {
        // Arrange: 100秒地点まで再生してクランプ基準を高い値にしておく
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, srcPtsSeconds: 100.0);
        clock.OnAudioWritten(SampleRate);
        Assert.Equal(101.0, clock.PositionAt(SampleRate), precision: 6);

        // Act: 10秒地点へ後方シーク → 錨 → 1秒再生
        clock.BeginSeek(10.0);
        Assert.Equal(10.0, clock.PositionAt(SampleRate)); // 保留中は target
        clock.AnchorAt(clock.WriteCursor, 10.0);
        clock.OnAudioWritten(SampleRate);

        // Assert: 単調クランプがシーク前の 101.0 に張り付かず、シーク先から進行する
        // （張り付くと映像側が全フレームを期限切れ判定して大量ドロップになる回帰バグ）
        Assert.Equal(11.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }
}
