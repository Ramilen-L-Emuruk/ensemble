using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Sync;

namespace MultiTrackPlayer.Tests.Sync;

/// <summary>
/// <see cref="PrerollGate"/> の状態遷移。実運用での呼び出し順は
/// <c>BeginSeek</c>（UI スレッド・採番前）→ <c>IssueEpoch</c>（demux スレッドの採番と同時）→
/// 各 <c>Notify</c>（デコードスレッド）。ここではその順序に加えて、
/// 順序が崩れた場合（採番前の通知・古い世代の通知）に取りこぼしも早期解除も起きないことを押さえる。
///
/// <para>
/// 検証の軸はミキサー出力保留の反映（<c>applyHold</c> に渡ってきた値）。判定と反映が
/// 分かれていると次のシークが立てた保留を旧シークの解除が上書きしうるため、
/// 「その操作から戻った時点で保留がどうなっているか」を毎回確かめる。
/// </para>
/// </summary>
public sealed class PrerollGateTests
{
    private static readonly SeekEpoch Old = new(3);
    private static readonly SeekEpoch Current = new(4);

    /// <summary>ミキサー出力保留への書き込みを順に記録する。</summary>
    private sealed class HoldRecorder
    {
        public List<bool> Applied { get; } = new();

        /// <summary>最後に反映された保留の状態。一度も書かれていなければ <c>null</c>。</summary>
        public bool? Last => Applied.Count == 0 ? null : Applied[^1];

        public void Apply(bool hold) => Applied.Add(hold);
    }

    /// <summary>映像・音声の両方を持つ通常のファイルでシークを開始した状態を作る。</summary>
    private static PrerollGate BeginSeekWithBothStreams(HoldRecorder recorder, SeekEpoch epoch)
    {
        var gate = new PrerollGate(recorder.Apply);
        gate.BeginSeek(hasVideo: true, hasAudio: true);
        gate.IssueEpoch(epoch);
        return gate;
    }

    [Fact(DisplayName = "作りたてのゲートは保留へ触れない")]
    public void NewGate_WithoutSeek_DoesNotTouchHold()
    {
        var recorder = new HoldRecorder();

        var gate = new PrerollGate(recorder.Apply);

        Assert.Empty(recorder.Applied);
        Assert.Null(gate.AwaitedEpoch);
    }

    [Fact(DisplayName = "シークを開始すると保留を立てる")]
    public void BeginSeek_WithBothStreams_AppliesHold()
    {
        var recorder = new HoldRecorder();
        var gate = new PrerollGate(recorder.Apply);

        bool held = gate.BeginSeek(hasVideo: true, hasAudio: true);

        Assert.True(held);
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "映像も音声も無い場合は保留しない")]
    public void BeginSeek_WithoutAnyStream_DoesNotHold()
    {
        var recorder = new HoldRecorder();
        var gate = new PrerollGate(recorder.Apply);

        bool held = gate.BeginSeek(hasVideo: false, hasAudio: false);

        Assert.False(held);
        Assert.False(recorder.Last);
    }

    [Fact(DisplayName = "世代が採番される前の通知は破棄する")]
    public void NotifyVideoReady_BeforeIssueEpoch_IsStale()
    {
        var recorder = new HoldRecorder();
        var gate = new PrerollGate(recorder.Apply);
        gate.BeginSeek(hasVideo: true, hasAudio: true);

        // BeginSeek から IssueEpoch までの間に届く通知は、前のシークが産んだもの。
        // デコードスレッド側の照合（プリロール世代 == キューの現在世代）は、demux が実際に
        // シークして Flush するまで古い世代のまま素通りするため、ここで落とす必要がある
        Assert.Equal(PrerollNotifyResult.Stale, gate.NotifyVideoReady(Old));
        Assert.Null(gate.AwaitedEpoch);
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "採番後に届いた古い世代の通知は破棄する")]
    public void NotifyAudioReady_WithOldEpoch_IsStale()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);

        Assert.Equal(PrerollNotifyResult.Stale, gate.NotifyAudioReady(Old));
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "片方だけ完了した段階では保留を解除しない")]
    public void NotifyVideoReady_WithAudioStillPending_KeepsHold()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);

        Assert.Equal(PrerollNotifyResult.Pending, gate.NotifyVideoReady(Current));
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "音声・映像がそろった時点で保留が解除されている")]
    public void NotifyBothReady_WithCurrentEpoch_ReleasesHold()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);

        Assert.Equal(PrerollNotifyResult.Pending, gate.NotifyAudioReady(Current));
        // Satisfied が返った時点で解除済みであること（呼び出し側が後から解除する形だと、
        // その隙間に次のシークが立てた保留を上書きしうる）
        Assert.Equal(PrerollNotifyResult.Satisfied, gate.NotifyVideoReady(Current));
        Assert.False(recorder.Last);
    }

    [Fact(DisplayName = "映像を持たないファイルでは音声の完了だけで解除する")]
    public void NotifyAudioReady_WithoutVideoStream_ReleasesHold()
    {
        var recorder = new HoldRecorder();
        var gate = new PrerollGate(recorder.Apply);
        gate.BeginSeek(hasVideo: false, hasAudio: true);
        gate.IssueEpoch(Current);

        Assert.Equal(PrerollNotifyResult.Satisfied, gate.NotifyAudioReady(Current));
        Assert.False(recorder.Last);
    }

    [Fact(DisplayName = "音声を持たないファイルでは映像の完了だけで解除する")]
    public void NotifyVideoReady_WithoutAudioStream_ReleasesHold()
    {
        var recorder = new HoldRecorder();
        var gate = new PrerollGate(recorder.Apply);
        gate.BeginSeek(hasVideo: true, hasAudio: false);
        gate.IssueEpoch(Current);

        Assert.Equal(PrerollNotifyResult.Satisfied, gate.NotifyVideoReady(Current));
        Assert.False(recorder.Last);
    }

    [Fact(DisplayName = "同じ側から二度通知されても解除の判断は変わらない")]
    public void NotifyVideoReady_Twice_StaysSatisfied()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);
        gate.NotifyAudioReady(Current);

        // EOF 到達による完了扱いと通常の完了が重なる経路がある。解除は冪等でなければならない
        Assert.Equal(PrerollNotifyResult.Satisfied, gate.NotifyVideoReady(Current));
        Assert.Equal(PrerollNotifyResult.Satisfied, gate.NotifyVideoReady(Current));
        Assert.False(recorder.Last);
    }

    [Fact(DisplayName = "次のシークが始まると、前のシークで受け付けた完了は無効になる")]
    public void BeginSeek_AfterPartialCompletion_ClearsPreviousProgress()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Old);
        Assert.Equal(PrerollNotifyResult.Pending, gate.NotifyAudioReady(Old));

        gate.BeginSeek(hasVideo: true, hasAudio: true);
        gate.IssueEpoch(Current);

        // 前のシークの音声完了を引き継いでいたら、ここで解除されてしまう
        Assert.Equal(PrerollNotifyResult.Pending, gate.NotifyVideoReady(Current));
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "通常の停止では保留を解除し、以後の通知は保留へ触れない")]
    public void Reset_WithoutHold_ReleasesHoldAndIgnoresLateNotifications()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);

        gate.Reset(hold: false);

        Assert.False(recorder.Last);
        Assert.Null(gate.AwaitedEpoch);

        // 遅れて届いた通知が保留へ触れないこと。書き込みが 1 件も増えないことで確かめる
        int writesBefore = recorder.Applied.Count;
        Assert.Equal(PrerollNotifyResult.Stale, gate.NotifyVideoReady(Current));
        Assert.Equal(PrerollNotifyResult.Stale, gate.NotifyAudioReady(Current));
        Assert.Equal(writesBefore, recorder.Applied.Count);
    }

    [Fact(DisplayName = "検疫時の消音では保留を立てたまま戻し、途中で解除を挟まない")]
    public void Reset_WithHold_KeepsHoldWithoutIntermediateRelease()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);
        int writesBefore = recorder.Applied.Count;

        gate.Reset(hold: true);

        // 取り残されたデコードスレッドが旧ファイルの音声を供給し続けているため、
        // 一度でも false を挟むとその隙間にミキサーの Read が拾って音が漏れる
        Assert.DoesNotContain(recorder.Applied.Skip(writesBefore), hold => !hold);
        Assert.True(recorder.Last);
        Assert.Null(gate.AwaitedEpoch);
        Assert.Equal(PrerollNotifyResult.Stale, gate.NotifyAudioReady(Current));
        Assert.True(recorder.Last);
    }

    [Fact(DisplayName = "待機世代は採番された世代をそのまま指す")]
    public void IssueEpoch_AfterBeginSeek_ExposesAwaitedEpoch()
    {
        var recorder = new HoldRecorder();
        PrerollGate gate = BeginSeekWithBothStreams(recorder, Current);

        Assert.Equal(Current, gate.AwaitedEpoch);
    }
}
