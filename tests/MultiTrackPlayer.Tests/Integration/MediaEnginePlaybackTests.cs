using System.Diagnostics;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Tests.Diagnostics;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 実パイプライン（demux・デコード・ミキサー・クロック）を <c>dotnet test</c> の中で動かす試験。
/// </summary>
/// <remarks>
/// <b>ここが他のテストと違う点</b>: FFmpeg のネイティブと実ファイルを使い、スレッドも本番と
/// 同じ構成で走る。差し替えているのは音声出力だけで、<b>時間の進みはテストが握る</b>
/// （<see cref="FakeAudioOutput.AdvanceMs"/>）。
/// <para>
/// <b>映像付きのファイルを開くのは 1 本だけ。</b> 映像を含むファイルを開くと共有 D3D11 デバイスが
/// 作られ HW デコードが選ばれるが、<b>1 プロセスで 2 つ目の <see cref="MediaEngine"/> が
/// 映像付きファイルを開くとプロセスが落ちる</b>（`.claude/REVIEW-REMEDIATION-STATUS.md` の
/// 「GPU デバイスの解放が決定的でない」を参照。本番はエンジンを 1 つしか作らないため
/// 表に出ていない）。そのため残りは音声のみのファイルを使う——<b>映像を伴う検証は
/// その欠陥を直してから足す。</b>
/// </para>
/// </remarks>
[Collection(DiagnosticLogCollection.Name)]
public sealed class MediaEnginePlaybackTests : IClassFixture<TestMediaFixture>
{
    /// <summary>
    /// 条件が成立するまで読み出しを続ける上限。デコードスレッドの立ち上がりを含むため、
    /// 実時間で余裕を持たせてある。
    /// </summary>
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(20);

    /// <summary>1 回の読み出しで進める時間。本番の WASAPI の 1 周期と同じ桁に合わせてある。</summary>
    private const int PumpStepMs = 20;

    private readonly TestMediaFixture _media;

    public MediaEnginePlaybackTests(TestMediaFixture media) => _media = media;

    [Fact(DisplayName = "映像と音声 3 トラックのファイルから尺・トラック数・寸法を読み取る",
          Skip = "映像付きファイルを開くと共有 D3D11 デバイスが作られ、解放が決定的でないため "
               + "GC のタイミングでテストホストが落ちる。`.claude/REVIEW-REMEDIATION-STATUS.md` の "
               + "「GPU デバイスの解放が決定的でない」を直してから外すこと")]
    public void Open_VideoWithThreeAudioTracks_ReportsDurationAndTracks()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;

        engine.Open(_media.ThreeTracks);

        Assert.NotNull(engine.CurrentMedia);
        Assert.Equal(3, engine.CurrentMedia!.AudioTracks.Count);
        // 生成側はフレーム境界で切り上げるため、ぴったり 3 秒にはならない
        Assert.InRange(engine.CurrentMedia.Duration.TotalSeconds, 2.5, 3.6);
        Assert.Equal(320, engine.CurrentMedia.Width);
        Assert.Equal(240, engine.CurrentMedia.Height);
    }

    [Fact(DisplayName = "音声のみのファイルでは映像の寸法が入らない")]
    public void Open_AudioOnlyFile_ReportsNoVideo()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;

        engine.Open(_media.AudioOnly);

        Assert.NotNull(engine.CurrentMedia);
        Assert.Single(engine.CurrentMedia!.AudioTracks);
        Assert.Equal(0, engine.CurrentMedia.Width);
        Assert.Equal(0, engine.CurrentMedia.Height);
    }

    [Fact(DisplayName = "読み出した音声の量に比例して再生位置が進む")]
    public void Play_AdvancesPositionInProportionToConsumedAudio()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        engine.Play();
        FakeAudioOutput output = harness.Output;
        Assert.True(output.IsPlaying, "Play() が音声出力を開始していない");

        // プリロールが解けて再生位置の基準が確定するまでは、位置は 0 のまま
        Pump(output, () => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        TimeSpan before = engine.Position;
        const int extraMs = 1000;
        int expectedBytesPerStep = output.BytesForMs(PumpStepMs);

        for (int consumed = 0; consumed < extraMs; consumed += PumpStepMs)
        {
            // **これが確かめているのは配線だけ**——要求した量がミキサーへ渡り、同じ量が
            // 戻ってくること。**供給の遅れはここでは検出できない。** `MultiTrackMixer.Read` は
            // 無音で埋めた場合も要求量を丸ごと返すため、デコードが追いつかなくても
            // この値は変わらない。遅れを見ているのはループの後の `Assert.InRange` だけで、
            // **あちらの許容幅を緩めると検出手段が無くなる**
            Assert.Equal(expectedBytesPerStep, output.AdvanceMs(PumpStepMs));
            // デコードスレッドに供給の機会を与える。譲らずに連打すると、供給が追いつかない分が
            // 無音で埋まり（`PlaybackClock.OnSilenceWritten` はレート 0 でセグメントを積む）
            // 位置の伸びが鈍る
            Thread.Sleep(1);
        }

        TimeSpan advanced = engine.Position - before;

        // **読み出した音声の量に比例して位置が進むこと。** ここが audio-master クロックの核で、
        // 「音は出ているのに位置が凍る」不具合をこのプロジェクトは 3 度出している。
        //
        // **許容幅をほぼ持たせていないのは実測の結果。** 構造上ちょうどになる——1 回の読み出しは
        // 7680 バイト（20ms 相当）で端数が無く、48kHz のフレーム換算でも割り切れるため、
        // 読み出した量がそのまま位置になる。±1ms は浮動小数の丸めぶんだけ。
        //
        // 測った範囲（すべて期待値ちょうどで通った）:
        // ・このテスト単独で 5 回。許容幅を ±0.0001ms に絞っても通る
        // ・全テスト（クラス並列＋メディア生成の FFmpeg エンコード込み）で 8 回
        // ・うち 4 回は Release の完全再ビルドと並行させて実行
        // **共有 CI ランナーのような環境では測っていない**が、このプロジェクトの CI は
        // `dotnet publish` だけでテストを走らせないため、影響するのは手元の環境に限られる。
        //
        // **足りない側へ外れたら、許容幅を広げずに理由を調べること。** 位置の伸びが短いのは
        // 「供給が追いつかず無音で埋まった」ことを意味する（`PlaybackClock.OnSilenceWritten` は
        // レート 0 でセグメントを積む）。**ここを緩めると、その劣化を見る手段が無くなる**
        //（ループ内のアサーションは配線しか見ていない）
        Assert.InRange(advanced.TotalMilliseconds, extraMs - 1, extraMs + 1);
    }

    [Fact(DisplayName = "一時停止すると音声の読み出しが止まる")]
    public void Pause_StopsConsumingAudio()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        engine.Play();
        FakeAudioOutput output = harness.Output;
        Pump(output, () => engine.Position > TimeSpan.Zero, "再生位置が進み始める");

        engine.Pause();
        Assert.Equal(PlaybackState.Paused, engine.State);

        long consumedAtPause = output.ConsumedBytes;
        Assert.Equal(0, output.AdvanceMs(PumpStepMs));
        Assert.Equal(consumedAtPause, output.ConsumedBytes);
    }

    [Fact(DisplayName = "音声出力の異常停止が失敗状態と利用者への通知になる")]
    public void PlaybackStopped_WithException_MarksFailureAndNotifies()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        string? notified = null;
        engine.PlaybackFailed += (_, message) => notified = message;
        Assert.False(engine.IsAudioOutputFailed, "開いた直後に失敗状態になっている");

        // **この経路は実デバイスでは起こせない**（デバイスが消える・音声サービスが止まる等）。
        // 偽の出力を差し替えたことで初めて決定的に踏めるようになった経路で、
        // ここが黙ると「音も再生位置も止まったのに理由がどこにも残らない」状態になる。
        // 目印を毎回変えるのは、fatal.log が過去の実行や他プロセスの行も持つため
        // （`testing.md`「対象システムが持つ静的・グローバルな状態に触るテスト」）
        string marker = $"テスト用の異常停止 {Guid.NewGuid():N}";
        harness.Output.RaisePlaybackStopped(new InvalidOperationException(marker));

        Assert.True(engine.IsAudioOutputFailed, "異常停止しても IsAudioOutputFailed が立たない");
        Assert.NotNull(notified);

        // **記録が常に残る側へ行くことまで確かめる。** ここを見ないと、`WriteFatal` が `Write`
        // （デバッグモード限定）へ格下げされても緑のまま通り、**既定運用では痕跡が 1 行も
        // 残らない**状態に気づけない
        Assert.Contains(marker, ReadFatalLog());
    }

    [Fact(DisplayName = "正常停止の通知は失敗として扱わない")]
    public void PlaybackStopped_WithoutException_DoesNotMarkFailure()
    {
        using var harness = new MediaEngineHarness();
        MediaEngine engine = harness.Engine;
        engine.Open(_media.AudioOnly);

        bool notified = false;
        engine.PlaybackFailed += (_, _) => notified = true;

        // Stop() / Dispose() による停止では Exception が null になる。ここを失敗扱いにすると
        // 通常の停止・ファイル切替のたびに利用者へ誤った失敗表示が出る
        harness.Output.RaisePlaybackStopped(null);

        Assert.False(engine.IsAudioOutputFailed);
        Assert.False(notified);
    }

    /// <summary>
    /// 常に残る側のログ（<c>fatal.log</c>）を読む。
    /// </summary>
    /// <remarks>
    /// <b>このクラスが <see cref="DiagnosticLogCollection"/> に属しているのが前提。</b>
    /// <c>WriteFatal</c> はセッションログが開いていればそちらへ書くため、
    /// <c>DiagnosticLog.Enable</c> を呼ぶクラスと並列に走ると記録がここへ来ない。
    /// <para>
    /// ファイル名を直接書いているのは、<c>DiagnosticLog</c> 側が非公開の定数として
    /// 持っているため。変えるときは両方直すこと。
    /// </para>
    /// </remarks>
    private static string ReadFatalLog()
    {
        string path = Path.Combine(DiagnosticLog.DefaultDirectory, "fatal.log");
        if (!File.Exists(path))
            Assert.Fail($"常に残る側のログが作られていない path={path}");

        // 実行中のアプリや他プロセスが追記していることがあるので共有して開く
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 条件が成立するまで、読み出しとデコードスレッドへの譲りを交互に行う。
    /// </summary>
    /// <remarks>
    /// <b>タイムアウトで必ず失敗させる。</b> 置かないと、条件が永久に成立しない不具合が
    /// 「ハング」として現れ、何が壊れたのか読めなくなる（ランナーのタイムアウト頼みになる）。
    /// </remarks>
    private static void Pump(FakeAudioOutput output, Func<bool> until, string what)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < PumpTimeout)
        {
            if (until()) return;
            output.AdvanceMs(PumpStepMs);
            // デコード・demux スレッドが前進する機会を作る。読み出しだけを回しても、
            // バッファを埋めるのは別スレッドなので条件は成立しない
            Thread.Sleep(5);
        }

        Assert.Fail($"{what} が {PumpTimeout.TotalSeconds} 秒で成立しなかった");
    }
}
