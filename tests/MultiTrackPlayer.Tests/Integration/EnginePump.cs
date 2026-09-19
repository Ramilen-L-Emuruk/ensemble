using System.Diagnostics;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 再生中の消費側（音声出力と UI の描画ループ）の代わりに、テストから一定の刻みで引く。
/// </summary>
/// <remarks>
/// <b>本番で引いているのは 2 つある。</b> 音声は WASAPI のレンダースレッドが
/// <c>Read</c> を呼び、映像（CPU 経路）は UI の <c>CompositionTarget.Rendering</c> が
/// <c>TryGetFrame</c> / <c>ReturnFrame</c> を呼ぶ。<b>片方だけを回すと本番と違う挙動になる</b>——
/// 映像を引かないと <c>CanObserveVideoStall</c> が「消費側が問いかけていない」と判断して
/// 映像の観測自体をやめる（<c>MediaEngine.VideoPullAliveWindowMs</c>）。
/// <para>
/// <b>テストが HWND を張らないため、映像はこの pull 型の経路に入る。</b>
/// <c>MediaEngine.IsVideoOutputActive</c> は <c>AttachVideoOutput</c> を呼んで初めて真になるので、
/// HW デコードでも（＝リングが GPU 側でも）提示は UI と同じ問いかけで進む。
/// <b>本番の GPU 経路にある vout スレッドの提示はここでは通らない</b>——
/// 見ているのはリングのリース／返却とシーク世代の扱いまで。
/// </para>
/// <para>
/// <b>リースは必ず返す。</b> 4 スロットしかないので、返し忘れると枯渇して映像が止まり、
/// 症状が「テストの失敗」ではなく「タイムアウト」として出る。
/// </para>
/// </remarks>
internal sealed class EnginePump(MediaEngine engine, FakeAudioOutput output)
{
    /// <summary>1 刻みで進める時間。本番の WASAPI の 1 周期と同じ桁。</summary>
    public const int StepMs = 20;

    /// <summary>
    /// <see cref="Until"/> の上限。デコードスレッドの立ち上がりを含むため実時間で余裕を持たせてある。
    /// </summary>
    private static readonly TimeSpan UntilTimeout = TimeSpan.FromSeconds(20);

    private readonly List<TimeSpan> _seenPts = [];

    /// <summary>映像フレームも引くか。音声のみのファイルでは意味が無いので落とせる。</summary>
    public bool PullVideo { get; init; } = true;

    /// <summary>
    /// これまでに提示されたフレームの表示時刻を、提示された順に返す。
    /// </summary>
    /// <remarks>
    /// <b>連続しているとは限らない。</b> 引く刻みより frame duration が短ければ間引かれ、
    /// その分は <c>PlaybackStatistics.DroppedFrames</c> 側に出る。
    /// </remarks>
    public IReadOnlyList<TimeSpan> SeenPts => _seenPts;

    /// <summary>1 刻みだけ進める。</summary>
    public void Step()
    {
        output.AdvanceMs(StepMs);
        if (PullVideo) PumpVideo();
        // demux・デコードスレッドが前進する機会を作る。読み出しだけを回しても、
        // バッファを埋めるのは別スレッドなので条件は成立しない
        Thread.Sleep(1);
    }

    /// <summary>条件が成立するまで進める。成立しなければ失敗させる。</summary>
    /// <remarks>
    /// <b>タイムアウトで必ず失敗させる。</b> 置かないと、条件が永久に成立しない不具合が
    /// 「ハング」として現れ、何が壊れたのか読めなくなる（ランナーのタイムアウト頼みになる）。
    /// </remarks>
    public void Until(Func<bool> until, string what)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < UntilTimeout)
        {
            if (until()) return;
            Step();
        }

        Assert.Fail($"{what} が {UntilTimeout.TotalSeconds} 秒で成立しなかった");
    }

    /// <summary>
    /// 実時間で <paramref name="realTime"/> ぶん進める。
    /// </summary>
    /// <remarks>
    /// <b>滞留検出の閾値を待つためにある。</b> 検出器の基準は <c>Environment.TickCount64</c> で、
    /// 引いた量ではなく実際の経過時間を見る。<b>刻みの数で代用してはいけない</b>——
    /// 1 刻みに要する実時間は環境で変わる。
    /// </remarks>
    public void For(TimeSpan realTime)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < realTime) Step();
    }

    /// <summary>
    /// 1 刻みで受け取る映像フレームの上限。<b>超えたら失敗させる。</b>
    /// </summary>
    /// <remarks>
    /// <b>この安全弁が無いと、下層の due 判定に回帰が入ったときハングする。</b>
    /// 同じクロック位置で際限なく due と判定され続ける形になると、
    /// <see cref="Until"/> のタイムアウトは<b>この内側のループには効かない</b>ため、
    /// 症状がランナーのタイムアウト頼みになって何が壊れたのか読めなくなる。
    /// <para>
    /// 値は<b>正常系では原理的に届かない大きさ</b>にしてある。試験用メディアは長いものでも
    /// 6 秒・25fps ＝ 150 枚しか無いので、1 刻みでこの枚数が出ることは（全フレームが
    /// 溜まっていても）起こらない。<b>緩いのは意図どおり</b>——ここは提示枚数の妥当性を
    /// 見る場所ではなく、ハングを失敗に変える場所。
    /// </para>
    /// </remarks>
    private const int MaxFramesPerStep = 1000;

    /// <summary>
    /// 映像を引く。<b>返却まで必ず行う。</b>
    /// </summary>
    /// <remarks>
    /// 1 刻みに複数枚提示されうるので、出なくなるまで引く（本番の UI は 1 フレームに 1 枚だが、
    /// ここは刻みが粗いため滞留させると溜まる）。
    /// </remarks>
    private void PumpVideo()
    {
        for (int i = 0; i < MaxFramesPerStep; i++)
        {
            VideoFrameLease? lease = engine.TryGetFrame(engine.Position);
            if (lease == null) return;

            _seenPts.Add(lease.Pts);
            engine.ReturnFrame(lease);

            // 一時停止中・停止中は保持フレームが繰り返し返るので 1 枚で切り上げる
            //（Playing 以外の TryGetFrame は _heldLease を返す経路）
            if (engine.State != PlaybackState.Playing) return;
        }

        Assert.Fail(
            $"1 刻みで {MaxFramesPerStep} 枚以上提示された。due 判定が同じ位置で"
            + "フレームを返し続けている疑い（このまま引き続けるとハングするので打ち切った）");
    }
}
