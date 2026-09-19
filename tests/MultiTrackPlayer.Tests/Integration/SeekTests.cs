using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Engine;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// シークを実パイプラインで通す試験。
/// </summary>
/// <remarks>
/// <b>このプロジェクトでシークが壊れる形は 2 通りある。</b> ひとつは<b>固まる</b>
/// （待ち合わせの取りこぼし・保留が解けない。<c>ensemble-review.md</c> §1）、
/// もうひとつは<b>シーク前の映像が残る</b>（シーク世代の取り違え。§6）。
/// どちらも「落ちない」ことだけを見ていると素通りするので、
/// <b>位置が進み続けること</b>と<b>提示されたフレームの表示時刻</b>を別々に見ている。
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class SeekTests : IClassFixture<TestMediaFixture>
{
    /// <summary>
    /// シークの着地点として認める幅（秒）。
    /// </summary>
    /// <remarks>
    /// <c>avformat_seek_file</c> はキーフレーム境界へ落ちるため、目標より手前に着地しうる。
    /// <see cref="TestMediaFixture.SmallGopVideo"/> のキーフレーム間隔は 0.2 秒なので、
    /// その 1 区間ぶんに少し余裕を足した値。
    /// </remarks>
    private const double LandingToleranceSeconds = 0.3;

    private readonly TestMediaFixture _media;

    public SeekTests(TestMediaFixture media) => _media = media;

    [Fact(DisplayName = "再生中のシークで再生位置が目標付近へ移る")]
    public void Seek_WhilePlaying_LandsNearTarget()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        var target = TimeSpan.FromSeconds(4);
        engine.Seek(target);

        // 着地は非同期（demux スレッドが avformat_seek_file を実行する）。
        // **実時間で待たずに条件で待つ**のは、遅いストレージでも安定させるため
        pump.Until(() => engine.Position >= target - TimeSpan.FromSeconds(LandingToleranceSeconds),
            "再生位置が目標付近へ移る");

        // 通り過ぎていないこと。ここを見ないと「シークが効かず末尾まで流れた」のを拾えない
        Assert.InRange(engine.Position.TotalSeconds,
            target.TotalSeconds - LandingToleranceSeconds, target.TotalSeconds + 1.0);
    }

    [Fact(DisplayName = "再生中のシーク後に、シーク前の映像フレームが提示されない")]
    public void Seek_WhilePlaying_DoesNotPresentFramesFromBeforeTarget()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => pump.SeenPts.Count >= 5, "映像フレームが提示され始める");

        var target = TimeSpan.FromSeconds(4);
        engine.Seek(target);
        pump.Until(() => engine.Position >= target - TimeSpan.FromSeconds(LandingToleranceSeconds),
            "再生位置が目標付近へ移る");

        // **着地してから先のフレームだけを見る。** 着地までの過渡状態
        //（クロックは目標へ飛んでいるがリングはまだ Flush されていない窓）は別問題で、
        // ここで見たいのは「落ち着いた後もシーク前のフレームが混ざり続けないか」
        int settled = pump.SeenPts.Count;
        pump.Until(() => pump.SeenPts.Count >= settled + 10, "着地後にさらに 10 枚提示される");

        double floor = target.TotalSeconds - LandingToleranceSeconds;
        for (int i = settled; i < pump.SeenPts.Count; i++)
        {
            // **これが §6 の回帰。** 世代を等値で見ずに下限比較へ緩めると、シーク前の残骸が
            // 新世代のフレームとして通過する（`e0fe085` で実際に起きた症状）
            Assert.True(pump.SeenPts[i].TotalSeconds >= floor,
                $"シーク前のフレームが提示された pts={pump.SeenPts[i].TotalSeconds:F3} "
                + $"target={target.TotalSeconds:F3}");
        }
    }

    [Fact(DisplayName = "シークを連打しても再生が止まらない")]
    public void Seek_Repeatedly_KeepsPlaying()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        // **連打はコアレスされる**（`DemuxThread.RequestSeek` が最新の目標だけを残す）。
        // ここで見たいのは「最後の目標へ行くこと」ではなく、**保留が解けて再生が続くこと**——
        // このプロジェクトは連続シーク・連打で固まる不具合を 4 件出している（§1）
        for (int i = 0; i < 10; i++)
        {
            engine.Seek(TimeSpan.FromSeconds(1 + (i % 4)));
            // 1 刻みだけ挟む。完全に間を置かないと demux スレッドが 1 度も走らず、
            // 「連打」ではなく「1 回のシーク」を試すことになる
            pump.Step();
        }

        var last = TimeSpan.FromSeconds(2);
        engine.Seek(last);
        pump.Until(() => engine.Position >= last - TimeSpan.FromSeconds(LandingToleranceSeconds),
            "最後のシークが着地する");

        // **着地しただけでは足りない。** 保留（`MultiTrackMixer.HoldOutput`）が解けていなければ
        // 音は出ておらず、位置もそこで凍る。伸びることまで確かめる
        TimeSpan afterLanding = engine.Position;
        pump.Until(() => engine.Position > afterLanding + TimeSpan.FromMilliseconds(200),
            "着地後に再生位置が伸びる");
        Assert.Equal(PlaybackState.Playing, engine.State);
    }

    [Fact(DisplayName = "一時停止中のシークで着地後のフレームを 1 枚だけ渡す")]
    public void Seek_WhilePaused_HandsOutOneFrame()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => pump.SeenPts.Count >= 5, "映像フレームが提示され始める");
        engine.Pause();

        var target = TimeSpan.FromSeconds(3);
        engine.Seek(target);

        // **再生中以外のシークは着地後の 1 枚を掴む**（`TryHoldNextFrame`）。
        // これが無いと、一時停止中にシークバーを動かしたとき画面が更新されない
        var held = engine.TryGetFrame(engine.Position);
        Assert.NotNull(held);
        Assert.True(held.Pts.TotalSeconds >= target.TotalSeconds - LandingToleranceSeconds,
            $"シーク前のフレームを掴んでいる pts={held.Pts.TotalSeconds:F3}");
        engine.ReturnFrame(held);

        // **2 枚目は返らない。** 保持フレームは「まだ消費していない」間だけ渡す作りで、
        // 毎回返す形にすると消費側が同じフレームを描き続ける
        Assert.Null(engine.TryGetFrame(engine.Position));
    }

    [Fact(DisplayName = "停止中のシークが次の再生の開始位置になる")]
    public void Seek_WhileStopped_BecomesNextPlayStartPosition()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.SmallGopVideo);

        // **再生していないので demux スレッドは無い。** この経路は要求を捨てていた時期があり、
        // 「つまみは動くのに何も起きない」状態になっていた
        TimeSpan? notified = null;
        engine.PositionChanged += (_, p) => notified = p;

        var target = TimeSpan.FromSeconds(3);
        engine.Seek(target);

        // 停止中は状態タイマーが動いていないので、ここで通知しないと時間表示だけ 0 に取り残される
        Assert.NotNull(notified);
        Assert.Equal(target, notified!.Value);

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        Assert.InRange(engine.Position.TotalSeconds,
            target.TotalSeconds - LandingToleranceSeconds, target.TotalSeconds + 1.0);
    }

    [Fact(DisplayName = "終端まで再生した後もう一度再生すると先頭から始まる")]
    public void Play_AfterPlaybackEnded_RestartsFromBeginning()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.ThreeTracks);

        bool ended = false;
        engine.PlaybackEnded += (_, _) => ended = true;

        engine.Play();
        var pump = new EnginePump(engine, harness.Output);
        pump.Until(() => ended, "終端に達する");

        // 終端では状態も進む。進めないと次の Play() が「既に Playing」で弾かれる
        Assert.Equal(PlaybackState.Stopped, engine.State);

        // **「最後まで見た動画をもう一度再生できない」を踏まないこと。** 開始位置の決定は
        // `PlaybackStartDecision`（単体試験あり）が担うが、ここで見ているのは<b>配線</b>——
        // 終端の状態から Play を押したときに実際に巻き戻ること
        engine.Play();
        Assert.Equal(PlaybackState.Playing, engine.State);
        pump.Until(() => engine.Position > TimeSpan.Zero, "再生位置がふたたび進み始める");
        Assert.InRange(engine.Position.TotalSeconds, 0.0, 1.0);
    }
}
