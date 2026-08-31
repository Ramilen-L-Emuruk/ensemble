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

    // ── IsSeekPending（「今は意図的に定数を返している」ことの公開）──
    //
    // MediaEngine.DetectClockStall が「位置が進んでいない」を異常と判断する側で、
    // 正常な待ち（着地待ち）を異常と呼ばないためにこれを見る。

    [Fact(DisplayName = "初期状態では着地待ちではない")]
    public void IsSeekPending_IsFalse_Initially()
    {
        var clock = new PlaybackClock(SampleRate);

        Assert.False(clock.IsSeekPending);
    }

    [Fact(DisplayName = "BeginSeek で着地待ちになり、AnchorAt で解ける")]
    public void IsSeekPending_IsTrue_BetweenBeginSeekAndAnchorAt()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate);

        clock.BeginSeek(30.0);
        Assert.True(clock.IsSeekPending);

        clock.AnchorAt(clock.WriteCursor, 30.0);
        Assert.False(clock.IsSeekPending);
    }

    [Fact(DisplayName = "着地待ちの間は位置が動かない（検出側が除外する根拠）")]
    public void IsSeekPending_WhileTrue_PositionStaysAtTarget()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate);

        clock.BeginSeek(30.0);
        clock.OnSilenceWritten(SampleRate);

        // 出力は進んでいるのに位置は目標のまま。これを「異常」と呼ばないために IsSeekPending が要る
        Assert.True(clock.IsSeekPending);
        Assert.Equal(30.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact(DisplayName = "Reset でも着地待ちは解ける")]
    public void IsSeekPending_IsFalse_AfterReset()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.BeginSeek(10.0);
        Assert.True(clock.IsSeekPending);

        clock.Reset();

        Assert.False(clock.IsSeekPending);
    }

    // ── 無音（アンダーラン）から実音声へ戻ったときの再開 ──
    //
    // 回帰: これが無いと、一度アンダーランしただけで以後ずっとメディア時刻が進まなくなる。
    // 音は鳴り writeCursor も hwFrames も伸びるのに、再生位置とシークバーだけが凍る
    // （実機では「短い音声ファイルを D&D するとシークバーが動かないことがある」として現れた）。

    [Fact(DisplayName = "無音の後に実音声が来れば位置が再開する")]
    public void OnAudioWritten_AfterSilence_ResumesPositionAdvance()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(atFrame: 0, srcPtsSeconds: 0.0);
        clock.OnAudioWritten(SampleRate / 2);   // 0.5 秒ぶん再生
        clock.OnSilenceWritten(SampleRate);     // 1 秒ぶんアンダーラン（時間は進まない）
        double frozen = clock.PositionAt(clock.WriteCursor);

        clock.OnAudioWritten(SampleRate);       // 実音声が戻る

        Assert.Equal(0.5, frozen, precision: 6);
        Assert.Equal(1.5, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact(DisplayName = "無音が錨より前に来た場合も位置は正しく進む")]
    public void OnAudioWritten_WhenSilencePrecedesAnchor_AdvancesFromAnchor()
    {
        // 不具合が「出るとき」と「出ないとき」を分けていた条件。こちらは AnchorAt が
        // 区間を立て直すため元から動いていた（同じ入力で結果が変わらないことを固定する）
        var clock = new PlaybackClock(SampleRate);
        clock.OnSilenceWritten(SampleRate);     // 錨より前の無音
        clock.AnchorAt(atFrame: clock.WriteCursor, srcPtsSeconds: 0.0);
        clock.OnAudioWritten(SampleRate);

        Assert.Equal(1.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact(DisplayName = "無音と実音声を繰り返しても、実音声のぶんだけ位置が進む")]
    public void OnAudioWritten_WithRepeatedUnderruns_AccumulatesOnlyAudio()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);

        for (int i = 0; i < 3; i++)
        {
            clock.OnAudioWritten(SampleRate);   // 1 秒ぶん再生
            clock.OnSilenceWritten(SampleRate); // 1 秒ぶん無音（進まない）
        }

        // 出力は 6 秒ぶん書かれたが、メディア時刻は実音声の 3 秒ぶんだけ進む
        Assert.Equal(6 * SampleRate, clock.WriteCursor);
        Assert.Equal(3.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }

    [Fact(DisplayName = "無音から戻るとき、再開するレートは再生速度に従う")]
    public void OnAudioWritten_AfterSilence_ResumesAtCurrentSpeed()
    {
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, 0.0);
        clock.OnAudioWritten(SampleRate);        // 1.0 秒（等速）
        clock.SetSpeedAt(clock.WriteCursor, 2.0);
        clock.OnSilenceWritten(SampleRate);      // アンダーラン
        clock.OnAudioWritten(SampleRate);        // 実音声が戻る（2 倍速）

        // 立て直すレートを 1.0 固定にすると、速度変更が無音で失われてここが 2.0 になる
        Assert.Equal(3.0, clock.PositionAt(clock.WriteCursor), precision: 6);
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

    [Fact]
    public void PositionAt_DuringHwLatencyTail_DoesNotPoisonClamp_ForNewSeekSegment()
    {
        // Arrange: 100秒地点まで再生してクランプ基準を高い値にしておく
        var clock = new PlaybackClock(SampleRate);
        clock.AnchorAt(0, srcPtsSeconds: 100.0);
        clock.OnAudioWritten(SampleRate); // writeCursor=48000, position=101.0

        // Act: 10秒地点へ後方シーク → 錨（この時点ではまだ新区間の音声は書かれていない）
        clock.BeginSeek(10.0);
        long anchorFrame = clock.WriteCursor; // 48000
        clock.AnchorAt(anchorFrame, 10.0);

        // WASAPI のレイテンシ分、HW はまだシーク前の音声を再生し終えていない過渡期を模す
        // （hwFrames が新セグメント開始フレームより手前）。この瞬間の raw が高い値でも許容する
        double duringLatencyTail = clock.PositionAt(anchorFrame - 1000);
        Assert.True(duringLatencyTail >= 10.0);

        // HW が新区間に追いついた
        clock.OnAudioWritten(SampleRate);

        // Assert: 過渡期の高い値が単調クランプの基準として焼き付いておらず、
        // 新しいシーク先から正しく進行する（焼き付くとクロックが古い位置に固まって
        // 映像が「期限切れ」判定され続け、リング満杯で完全停止する回帰バグ）
        Assert.Equal(11.0, clock.PositionAt(clock.WriteCursor), precision: 6);
    }
}
