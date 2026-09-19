using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 滞留検出 3 つ（音声・映像・クロック）を実パイプラインで踏む試験。
/// </summary>
/// <remarks>
/// <b>3 つに分かれている理由がここで確かめられる。</b> 音声が止まれば位置も映像も止まるが、
/// <b>音声も映像も動き続けたままクロックだけが凍る経路がある</b>ので 3 つ目が要る
/// （<c>MediaEngine.DetectClockStall</c> の doc）。それぞれを別々に作れることが、
/// 検出器を分けている意味そのもの。
/// <para>
/// <b>閾値は短くして渡す</b>（<see cref="StallTimings"/>）。本番の 3 秒をそのまま待つと
/// 1 本で 10 秒級になる。<b>短くしても踏む経路は同じ</b>——判定は
/// <c>Environment.TickCount64</c> の差と閾値の比較だけで、値によって分岐しない。
/// </para>
/// <para>
/// <b>検出器ごとに違う閾値を渡しているのは、どの検出器が鳴ったかを記録から見分けるため</b>
/// （記録の文面には実際に使った閾値が入る）。<b>これだけでは足りない</b>——
/// <c>fatal.log</c> は追記式で消えないので、閾値を目印にするだけでは
/// <b>前回の実行で自分が書いた行</b>に当たる。位置を覚えてそれ以降だけを見ること
/// （<see cref="FatalLog.Bookmark"/>。実際に一度これで、検出器を丸ごと止めても
/// 緑のまま通るテストになっていた）。
/// </para>
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class StallDetectionTests : IClassFixture<TestMediaFixture>
{
    /// <summary>
    /// 音声の滞留を試すときの閾値。<b>3 つで別の値にしてある</b>（クラスの remarks 参照）。
    /// </summary>
    private const int AudioThresholdMs = 211;

    /// <summary>映像の滞留を試すときの閾値。</summary>
    private const int VideoThresholdMs = 223;

    /// <summary>クロックの滞留を試すときの閾値。</summary>
    private const int ClockThresholdMs = 233;

    /// <summary>
    /// シーク後の猶予。<b>閾値より短くしないと、猶予の内側で観測が止まったまま終わる。</b>
    /// </summary>
    private const int GraceMs = 100;

    /// <summary>
    /// 閾値を超えたことが状態タイマー（100ms 周期）に拾われるまでの待ち。
    /// </summary>
    /// <remarks>
    /// 閾値の 3 倍に取ってある。<b>刻みの数ではなく実時間で待つ</b>のは、検出器の基準が
    /// <c>Environment.TickCount64</c> で、1 刻みに要する実時間が環境で変わるため。
    /// </remarks>
    private static TimeSpan SettleFor(int thresholdMs) =>
        TimeSpan.FromMilliseconds(thresholdMs * 3);

    private static StallTimings Timings => new(
        AudioThresholdMs: AudioThresholdMs,
        VideoThresholdMs: VideoThresholdMs,
        ClockThresholdMs: ClockThresholdMs,
        PrerollGraceMs: GraceMs);

    private readonly TestMediaFixture _media;

    public StallDetectionTests(TestMediaFixture media) => _media = media;

    [Fact(DisplayName = "音声の読み出しが止まると滞留として記録される")]
    public void AudioStall_WhenReadStops_IsRecorded()
    {
        using var harness = new MediaEngineHarness(Timings);
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output) { PullVideo = false };
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");
        Assert.False(engine.IsAudioStalled, "再生できている間に滞留と判定されている");

        // **記録を探す範囲をここから後ろに限る。** `fatal.log` は追記式で消えないので、
        // 素で「含まれている」を見ると**前回の実行で自分が書いた行**に当たる（実際にそれで、
        // 検出器を丸ごと止めても緑になるテストになっていた）
        long mark = FatalLog.Bookmark();

        // **引くのをやめる。** 実デバイスでこれが起きるのは、音声サービスが応答しなくなる・
        // デバイスが取り外される類の場面で、**例外が飛ばないので他のどの経路も気づけない**
        Thread.Sleep(SettleFor(AudioThresholdMs));

        Assert.True(engine.IsAudioStalled, "読み出しを止めても滞留と判定されない");
        FatalLog.WaitForContains(mark, $"閾値 {AudioThresholdMs}ms", "音声の滞留");

        // **読み出しを戻せば自動で解けること。** ここが解けないと、一度詰まっただけで
        // 以後ずっと滞留扱いになる（寿命の長いオブジェクトに「一度だけ」のフラグを
        // 当てたときの壊れ方。`ensemble-review.md` §7）
        pump.Until(() => !engine.IsAudioStalled, "読み出しを戻すと滞留が解ける");
    }

    [Fact(DisplayName = "読み出しは続いているのに再生位置が凍るとクロックの滞留として記録される")]
    public void ClockStall_WhenPositionFreezes_IsRecorded()
    {
        using var harness = new MediaEngineHarness(Timings);
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output) { PullVideo = false };
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        // 記録を探す範囲をここから後ろに限る（理由は音声側のテストのコメント）
        long mark = FatalLog.Bookmark();

        // **読み出しは止めない。** これが 3 つ目の検出器が要る理由そのもの——音声は流れ続けるので
        // 音声の滞留は鳴らず、映像も（あれば）提示され続ける。位置だけが凍る
        harness.Output.FreezePosition();
        pump.For(SettleFor(ClockThresholdMs));

        Assert.False(engine.IsAudioStalled, "音声は流れているのに音声の滞留として鳴っている");
        FatalLog.WaitForContains(mark, $"閾値 {ClockThresholdMs}ms", "クロックの滞留");

        // 位置が戻れば回復の記録が出る。**開始だけの記録は意味が定まらない**ので、
        // 対になる行が出ることまで見る（`MediaEngine.RecordStallAbandoned` の doc）
        long recoveryMark = FatalLog.Bookmark();
        harness.Output.UnfreezePosition();
        pump.For(SettleFor(ClockThresholdMs));
        FatalLog.WaitForContains(
            recoveryMark, "再生位置の進行が回復した", "クロックの滞留からの回復");
    }

    [Fact(DisplayName = "映像が音声より早く終わるファイルは映像の滞留として記録される（既知の誤検知）")]
    public void VideoStall_WhenVideoStreamEndsBeforeAudio_IsRecorded()
    {
        using var harness = new MediaEngineHarness(Timings);
        MediaEngine engine = harness.Engine;
        engine.Open(_media.VideoEndsEarly);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => pump.SeenPts.Count >= 3, "映像フレームが提示され始める");

        // 記録を探す範囲をここから後ろに限る（理由は音声側のテストのコメント）
        long mark = FatalLog.Bookmark();

        // **問いかけは続けたまま映像だけを枯らす。** 引くのをやめると
        // `CanObserveVideoStall` が「消費側が問いかけていない」と判断して観測自体をやめるため、
        // その方法では作れない（映像が短いファイルを用意しているのはこのため）
        pump.For(SettleFor(VideoThresholdMs));

        Assert.True(engine.IsVideoStalled, "映像が枯れても滞留と判定されない");
        FatalLog.WaitForContains(mark, $"閾値 {VideoThresholdMs}ms", "映像の滞留");

        // **この状況を「誤検知」と呼んでいるのは実装側の判断で、テストはそれを固定している。**
        // リングが EOF でも抑制しないのは、デコードスレッドの異常終了（`MarkEof` を呼ぶ経路）を
        // 黙らせないため——理由は `MediaEngine.DetectVideoStall` の remarks が単一の情報源。
        // **抑制を入れる変更をするとこのテストが落ちる。** そのとき見直すのはテストではなく、
        // あの remarks が挙げている代償の方（`ensemble-review.md` §7 の「除外条件が
        // 症状そのものを指していないか」）
    }
}
