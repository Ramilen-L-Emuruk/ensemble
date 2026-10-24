using System.Diagnostics;
using System.Windows.Threading;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.UI.Diagnostics;

/// <summary>
/// UI スレッドが処理を受け付けるまでの遅れを定期的に測り、長引いたときだけ診断ログへ残す。
/// </summary>
/// <remarks>
/// <para>
/// <b>映像の子ウィンドウ（GPU 経路の提示先）は UI スレッドが所有している。</b> UI スレッドが詰まると
/// 提示側が待たされうるため、映像の停止を調べるときに UI スレッドの応答を併せて見られるようにする。
/// </para>
/// <para>
/// 測り方: 別スレッドのタイマーから UI スレッドへ空の処理を投げ、実際に実行されるまでの時間を見る。
/// 前に投げた処理がまだ実行されていなければ重ねて投げない。詰まりが解けたら、詰まっていた長さを 1 行残す。
/// <b>解けないまま続く詰まりは、タイマー側から「応答していない（継続中）」を一定間隔で残す</b>
/// （解けた後にしか書かない作りだと、最も重い固まりほど記録が空になる）。
/// </para>
/// <para>
/// 値は下限として読む。投げるのは測る間隔ごとなので、詰まり始めから投げるまでの最大 1 間隔ぶんは数えない。
/// また通常の UI 処理と同じ優先度で測るので、入力の遅れはこれより小さいことがある。
/// 診断ログが無効の間は何もしない。
/// </para>
/// </remarks>
internal sealed class UiResponsivenessProbe : IDisposable
{
    /// <summary>測る間隔。</summary>
    private const int ProbeIntervalMs = 250;

    /// <summary>これ以上待たされたら記録する。人が引っかかりを感じ始める長さを目安にしている。</summary>
    private const double SlowThresholdMs = 100.0;

    /// <summary>応答が戻らないまま続いている間、継続中の行を残す間隔。</summary>
    private const double StillWaitingReportIntervalMs = 1000.0;

    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    // 投げた処理がまだ実行されていなければ 1
    private int _pending;
    // 投げた時刻（Stopwatch のティック）。待ちの間のコールバックが継続中の長さを測るのに使う。
    // 投げていない間と、確保した直後でまだ書いていない間は 0（その間は測らない）。_pending を下ろす前に 0 へ戻す
    private long _postedTimestamp;
    // 継続中の行を最後に書いた時点の待ち時間（ミリ秒）。投入を確保したコールバックが 0 に戻し、
    // 待ちの間のコールバックが更新する（コールバックが重なっても値が少しずれるだけで、記録の間隔の目安にしか使わない）
    private double _lastStillWaitingReportMs;
    // 投入の失敗を記録済みか。失敗が続く間は 1 行だけ残し、成功したら下ろす
    private bool _postFailureLogged;
    private volatile bool _disposed;

    public UiResponsivenessProbe(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new Timer(_ => Probe(), null, ProbeIntervalMs, ProbeIntervalMs);
    }

    private void Probe()
    {
        if (_disposed || !DiagnosticLog.Enabled) return;
        // 終了処理に入った UI スレッドは投げた処理を実行しない。測り続けると「応答していない」を偽って記録する
        if (_dispatcher.HasShutdownStarted) return;

        // 前に投げた処理がまだ実行されていなければ重ねて投げない。判定と確保は 1 操作で行う
        //（タイマーのコールバックは遅れると重なって走りうる）
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
        {
            ReportIfStillWaiting();
            return;
        }

        long posted = Stopwatch.GetTimestamp();
        Volatile.Write(ref _postedTimestamp, posted);
        _lastStillWaitingReportMs = 0;
        try
        {
            // 通常の UI 処理（バインド・イベントハンドラ）と同じ優先度で測る
            DispatcherOperation op = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                double delayMs = ElapsedMs(posted);
                Release();
                if (delayMs >= SlowThresholdMs)
                    DiagnosticLog.Write("uiLag", $"UI スレッドが {delayMs:F0}ms 応答しなかった");
            });
            // 終了処理と入れ違った場合は実行されないまま中断される。待ちとして数えない
            if (op.Status == DispatcherOperationStatus.Aborted) Release();
            _postFailureLogged = false;
        }
        catch (Exception ex)
        {
            // タイマーのコールバックから例外を漏らすとプロセスが落ちる。診断のためにそれは割に合わないので、
            // 記録して次の周でやり直す（失敗が続く間は 1 行だけ）
            Release();
            if (_postFailureLogged) return;
            _postFailureLogged = true;
            DiagnosticLog.Write("uiLag", $"UI スレッドへの計測の投入に失敗: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 投入時刻を先に 0 へ戻してから「投げていない」にする。逆にすると、次に確保した周がまだ時刻を書いていない間に
    // 待ちのコールバックが古い時刻を読み、偽の「応答していない」を書く
    private void Release()
    {
        Volatile.Write(ref _postedTimestamp, 0);
        Volatile.Write(ref _pending, 0);
    }

    private void ReportIfStillWaiting()
    {
        long posted = Volatile.Read(ref _postedTimestamp);
        if (posted == 0) return;
        double waitedMs = ElapsedMs(posted);
        if (waitedMs < StillWaitingReportIntervalMs) return;
        if (waitedMs - _lastStillWaitingReportMs < StillWaitingReportIntervalMs) return;
        _lastStillWaitingReportMs = waitedMs;
        DiagnosticLog.Write("uiLag", $"UI スレッドが {waitedMs:F0}ms 以上応答していない（継続中）");
    }

    private static double ElapsedMs(long fromTimestamp) =>
        (Stopwatch.GetTimestamp() - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        // 破棄の後にキュー済みのコールバックが 1 回走りうるので、先にフラグで止める
        _disposed = true;
        _timer.Dispose();
    }
}
