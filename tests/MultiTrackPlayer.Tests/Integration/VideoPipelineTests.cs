using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Engine;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 映像を伴う実パイプラインの試験。フレームが実際に提示されることを見る。
/// </summary>
/// <remarks>
/// <b>ここが通ることは「映像が正しく見える」ことを意味しない。</b> 見ているのはリングから
/// フレームが出てくること・表示時刻が進むこと・リースが枯れないことまでで、
/// <b>描画（<c>Rendering/</c>）は HWND を要するため対象外</b>。色・向き・引っかかりは
/// 人が触るまで出てこない（<c>ensemble-review.md</c> §5）。
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class VideoPipelineTests : IClassFixture<TestMediaFixture>
{
    private readonly TestMediaFixture _media;

    public VideoPipelineTests(TestMediaFixture media) => _media = media;

    [Fact(DisplayName = "映像付きファイルの再生でフレームが提示され、表示時刻が進む")]
    public void Play_VideoFile_PresentsFramesWithAdvancingPts()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);
        Assert.NotNull(engine.CurrentMedia);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);

        // 5 枚集まれば「流れている」と言える。1 枚だけだと、シーク後に 1 枚表示する経路
        //（TryHoldNextFrame）でも成立してしまう
        pump.Until(() => pump.SeenPts.Count >= 5, "映像フレームが 5 枚提示される");

        // **表示時刻が単調に増えること。** ここが崩れるのはシーク世代の取り違えの典型
        //（§6。古い世代の残骸が新しいフレームに混ざる）
        for (int i = 1; i < pump.SeenPts.Count; i++)
        {
            Assert.True(pump.SeenPts[i] > pump.SeenPts[i - 1],
                $"表示時刻が戻った {i - 1}={pump.SeenPts[i - 1]} {i}={pump.SeenPts[i]}");
        }

        // 先頭から始まっていること。最初のフレームが 1 秒も先だと、プリロールの取りこぼし
        Assert.InRange(pump.SeenPts[0].TotalSeconds, 0.0, 0.5);
    }

    [Fact(DisplayName = "音声のみのファイルでは映像フレームが 1 枚も出ない")]
    public void Play_AudioOnly_PresentsNoFrames()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");
        pump.For(TimeSpan.FromMilliseconds(200));

        // **リングそのものが作られないこと。** 空のリングを作って毎回 null を返す形だと、
        // 滞留検出が「映像が出ていない」と誤って鳴る（CanObserveVideoStall は ring != null を見る）
        Assert.Empty(pump.SeenPts);
        Assert.False(engine.IsVideoStalled);
    }

    [Fact(DisplayName = "映像フレームを長く引き続けてもリースが枯れない")]
    public void Play_LongPull_DoesNotExhaustLeases()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => pump.SeenPts.Count >= 5, "映像フレームが提示され始める");

        int before = pump.SeenPts.Count;
        // **リースの漏れは「4 枚で止まる」として現れる。** スロットは 4 つしかないので、
        // 返却を 1 度でも落とすと以後フレームが 1 枚も出なくなる（過去に実際に起きた。
        // TryGetFrame と ReturnFrame の間に状態遷移が挟まった経路）。
        // 20 枚は 4 スロットを 5 周する量で、漏れていれば必ず止まる
        pump.Until(() => pump.SeenPts.Count >= before + 20, "さらに 20 枚提示される");
    }

    [Fact(DisplayName = "一時停止中は新しいフレームを渡さない")]
    public void Pause_HandsOutNoNewFrame()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => pump.SeenPts.Count >= 5, "映像フレームが提示され始める");

        engine.Pause();
        Assert.Equal(PlaybackState.Paused, engine.State);

        // **一時停止で画面が残るのは「渡すから」ではなく「描き直さないから」。**
        // 保持フレーム（`_heldLease`）を掴むのはシーク（再生中以外）とコマ送りだけで、
        // 素の `Pause` は掴まない。消費側は null を受けて前の表示をそのまま残す。
        //
        // **ここを「1 枚返るべき」と読み替えないこと。** 渡す形にすると、返却されない
        // リースがスロットを 1 つ占め続ける（スロットは 4 つしかない）。
        // 実際にこのテストを書き始めたとき、その思い込みで assert を逆に書いた
        Assert.Null(engine.TryGetFrame(engine.Position));
    }
}
