using MultiTrackPlayer.Core.Models;
using Xunit;

namespace MultiTrackPlayer.Tests.Playback;

/// <summary>
/// <see cref="PlaybackStartDecision"/> の判断を検証する。判断材料が 5 つあり、組み合わせの
/// 取り違えが「もう一度再生できない」「シーク直後に先頭へ戻される」といった不具合に直結する。
/// </summary>
public sealed class PlaybackStartDecisionTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>停止からの再生。個別のテストで上書きしたい条件だけを渡す。</summary>
    private static PlaybackStartDecision DecideFromStopped(
        bool pipelineWasFresh = false, bool restartFromEof = false, bool rewindSkipped = false,
        TimeSpan? pendingStart = null)
        => PlaybackStartDecision.Decide(
            wasStopped: true, pipelineWasFresh, restartFromEof, rewindSkipped, pendingStart);

    [Fact(DisplayName = "一時停止からの再開では位置に触らない")]
    public void Decide_WhenResumingFromPause_ReturnsNone()
    {
        var decision = PlaybackStartDecision.Decide(
            wasStopped: false, pipelineWasFresh: false, restartFromEof: false, rewindSkipped: false,
            pendingStart: null);

        Assert.Equal(PlaybackStartAction.None, decision.Action);
    }

    [Fact(DisplayName = "一時停止からの再開では保留中の開始位置を使わない")]
    public void Decide_WhenResumingFromPauseWithPendingStart_ReturnsNone()
    {
        // 一時停止中はパイプラインが生きているので保留は生じないが、生じても飛ばないことを固定する
        var decision = PlaybackStartDecision.Decide(
            wasStopped: false, pipelineWasFresh: false, restartFromEof: false, rewindSkipped: false,
            pendingStart: FiveMinutes);

        Assert.Equal(PlaybackStartAction.None, decision.Action);
    }

    [Fact(DisplayName = "停止中に受けたシーク位置へシークする")]
    public void Decide_WithPendingStart_SeeksToIt()
    {
        var decision = DecideFromStopped(pipelineWasFresh: true, pendingStart: FiveMinutes);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(FiveMinutes, decision.Target);
    }

    [Fact(DisplayName = "保留中の開始位置が 0 秒でもシークとして扱う")]
    public void Decide_WithPendingStartAtZero_SeeksToZero()
    {
        // 「保留なし」と「先頭を明示的に選んだ」を取り違えると、錨だけ張って読み取り位置を
        // 戻さない経路へ落ちる
        var decision = DecideFromStopped(pipelineWasFresh: true, pendingStart: TimeSpan.Zero);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Target);
    }

    // 以下 2 件は、呼び出し元の不変条件が崩れた場合に備えた防御的な固定。
    // 現在の MediaEngine では、保持位置が設定されるのは demux スレッドが居ないときだけなので
    // 「保持位置あり」と「巻き戻し省略／終端からの再開」は同時に起こらない。それでも優先順位を
    // 決めておくのは、利用者が明示した位置を他の事情で上書きしないことをここで保証するため

    [Fact(DisplayName = "巻き戻しを省略していても保留中の開始位置を優先する")]
    public void Decide_WithPendingStartAndRewindSkipped_SeeksToPendingStart()
    {
        var decision = DecideFromStopped(rewindSkipped: true, pendingStart: FiveMinutes);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(FiveMinutes, decision.Target);
    }

    [Fact(DisplayName = "終端からの再開でも保留中の開始位置を優先する")]
    public void Decide_WithPendingStartAndRestartFromEof_SeeksToPendingStart()
    {
        var decision = DecideFromStopped(restartFromEof: true, pendingStart: FiveMinutes);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(FiveMinutes, decision.Target);
    }

    [Fact(DisplayName = "終端から再生を押したら先頭へシークする")]
    public void Decide_WhenRestartingFromEof_SeeksToStart()
    {
        var decision = DecideFromStopped(restartFromEof: true);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Target);
    }

    [Fact(DisplayName = "読み取り位置を戻せていなければ先頭へシークする")]
    public void Decide_WhenRewindSkipped_SeeksToStart()
    {
        // 錨だけ張ると、実際の内容は停止位置からなのに表示だけ 0 秒から進む食い違いになる
        var decision = DecideFromStopped(pipelineWasFresh: true, rewindSkipped: true);

        Assert.Equal(PlaybackStartAction.SeekTo, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Target);
    }

    [Fact(DisplayName = "畳んだ状態からの新規開始では錨だけ張る")]
    public void Decide_WithFreshPipeline_AnchorsAtStart()
    {
        var decision = DecideFromStopped(pipelineWasFresh: true);

        Assert.Equal(PlaybackStartAction.AnchorAtStart, decision.Action);
    }

    [Fact(DisplayName = "終端到達後に手動シークしてから再生した場合は位置に触らない")]
    public void Decide_AfterManualSeekFollowingEof_ReturnsNone()
    {
        // パイプラインは生きており（pipelineWasFresh=false）、そのシークが正しい錨を張っている。
        // ここで 0 秒として上書きすると、シークした位置ではなく先頭から始まる
        var decision = DecideFromStopped();

        Assert.Equal(PlaybackStartAction.None, decision.Action);
    }
}
