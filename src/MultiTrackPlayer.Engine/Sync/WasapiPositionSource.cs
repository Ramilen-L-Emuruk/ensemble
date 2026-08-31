using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Sync;

/// <summary>
/// WasapiOut.GetPosition()（IWavePosition, OutputWaveFormat 単位のバイト位置）を
/// mixer サンプル軸（48kHz）のフレーム数へ写像する。
/// 単調性・write cursor との整合性を継続的に検査し、異常が連続したら FallbackPositionSource へ自動切替する。
/// </summary>
/// <remarks>
/// <para>
/// <b>このクラスは複数スレッドから同時に呼ばれる。</b> 呼び出し元は 3 経路あるが、
/// <b>同時に走るのは最大 2 つ</b>——映像側のどちらか一方と、状態タイマー。
/// </para>
/// <list type="bullet">
/// <item>vout スレッド（GPU 経路で<b>毎 vsync</b>）</item>
/// <item>UI スレッド（CPU 経路で<b>毎フレーム</b>、<c>MediaEngine.Position</c> 経由）</item>
/// <item>状態タイマー（100ms 周期）</item>
/// </list>
/// <para>
/// <b>映像側の 2 つは、定常状態では排他。</b> UI 側は <c>IsVideoOutputActive</c> が真なら映像プルを
/// 行わず（<c>MainWindow.RenderNextFrame</c> の早期 return）、そのフラグは vout スレッドが
/// 動いていることそのものを指す。だから合計は 60 + 60 + 10 ではなく<b>毎秒 70 回前後</b>になる。
/// </para>
/// <para>
/// <b>ただし過渡状態では 3 つが重なる。</b> ①vout の開始では <c>_swapPresenter</c> の代入が
/// スレッド起動より先なので、直前にフラグを偽と読んで進行中だった UI の 1 フレームが重なる
/// （〜16ms）。②<c>StopVideoOutput</c> の停止待ちがタイムアウトすると、<c>_swapPresenter</c> を
/// <c>null</c> にして UI が再開した後もゾンビの vout スレッドが回り続ける（<b>こちらは短くない</b>）。
/// <b>ロックは競合の本数に依存しないので結論は変わらない</b>が、
/// 「最大 2 つ」を前提にした最適化を後から入れないこと。
/// </para>
/// <para>
/// <b>だから検査状態はロックで守る。</b> 守らないと<b>インターリーブそれ自体が違反を生む</b>——
/// A が位置 X を読み、B が後から Y（&gt;X）を読んで先に <c>_lastFrames = Y</c> を書くと、
/// A の単調性判定が <c>X &gt;= Y</c> で偽になる。ハードウェアの位置は単調なのに、
/// 読む順序と書く順序が食い違うだけで違反になる。
/// </para>
/// <para>
/// <b>被害は「フォールバックへ落ちる」ではなく「古い位置が返る」。</b> 偽の違反が起きた呼び出しは
/// 新しい値ではなく <c>_lastFrames</c> を返すため、<b>vout スレッドの due 判定が一瞬だけ古い
/// クロックで走る</b>（基準そのものが巻き戻ることもある）。<see cref="ViolationThreshold"/> 回
/// <b>連続</b>すれば恒久フォールバックだが、正常な値が 1 度返れば数えは振り出しに戻るので、
/// そこまで至るには違反が固まって起きる必要がある。
/// <b>ロックを外して 2 スレッド・4 万回で実測したところ、違反は 1 件・切替には至らなかった。</b>
/// 頻度は低い——それでも直すのは、<b>正しさの代償がロック 1 つ</b>だから。
/// </para>
/// </remarks>
public sealed class WasapiPositionSource : IPlaybackPositionSource
{
    private const int ViolationThreshold = 5;

    private readonly IWavePosition _wavePosition;
    private readonly WaveFormat _outputFormat;
    private readonly int _sampleRate;
    private readonly Func<long> _getWriteCursorFrames;
    private readonly FallbackPositionSource _fallback;

    /// <summary>
    /// 検査状態（<see cref="_fallbackActive"/> / <see cref="_violationCount"/> /
    /// <see cref="_lastFrames"/>）と <see cref="_fallback"/> を守る。理由はクラスの remarks。
    /// </summary>
    /// <remarks>
    /// <b>ロックの順序</b>: このロック → <c>PlaybackClock</c> のロック
    /// （<see cref="_getWriteCursorFrames"/> 経由）。逆向きに取る経路は無い
    /// （<c>PlaybackClock</c> は位置ソースを参照しない）。
    /// <para>
    /// <b><see cref="_fallback"/> 自身はロックを持たない。</b> 到達経路がこのクラスの
    /// ロック内だけなので要らない（外から直に構築している箇所は無い）。
    /// 直接使う経路を作るなら、あちらにも同じ守りが必要になる。
    /// </para>
    /// </remarks>
    private readonly object _lock = new();

    private bool _fallbackActive;
    private int _violationCount;
    private long _lastFrames;

    public bool IsFallbackActive { get { lock (_lock) return _fallbackActive; } }

    public WasapiPositionSource(IWavePosition wavePosition, WaveFormat outputFormat, int sampleRate,
        Func<long> getWriteCursorFrames, double latencySeconds)
    {
        _wavePosition = wavePosition;
        _outputFormat = outputFormat;
        _sampleRate = sampleRate;
        _getWriteCursorFrames = getWriteCursorFrames;
        _fallback = new FallbackPositionSource(getWriteCursorFrames, latencySeconds, sampleRate);
    }

    /// <remarks>
    /// <b>サンプリングもロック内で行うこと。</b> ハードウェア位置の取得だけ外へ出すと軽くなるが、
    /// <b>2 スレッドの「読んだ順」と「検証した順」が入れ替わる</b>ため、クラスの remarks に書いた
    /// 偽の違反がそのまま残る。ロックを置く意味が無くなる。
    /// <para>
    /// COM 呼び出し（<c>IAudioClock::GetPosition</c>）を抱える。WASAPI の
    /// <c>IAudioClock</c> はフリースレッドマーシャラを持つのでスレッド境界での待ちは通常起きないが、
    /// <b>ドライバ不調でこの呼び出し自体が長くブロックする可能性は排除できない</b>
    /// （ロック導入以前も 3 経路が無ロックでこれを呼んでいたので、悪化ではなく既存の前提）。
    /// <para>
    /// <b>ロック内からログを直接書かないことの方が重要。</b> 常に残る側は
    /// <see cref="Diagnostics.DiagnosticLog.WriteFatalDeferred"/>、診断ログ側も
    /// <see cref="Diagnostics.DiagnosticLog.WriteDeferred"/> へ逃がしている
    /// （あちらは有効時に <c>AutoFlush</c> つきの書き込みを行うため、
    /// デバッグモードを有効にした瞬間だけ現れる遅さになる）。
    /// </para>
    /// </remarks>
    public long GetPositionFrames()
    {
        lock (_lock)
        {
            if (_fallbackActive) return _fallback.GetPositionFrames();

            long bytes = _wavePosition.GetPosition();
            double seconds = bytes / (double)_outputFormat.AverageBytesPerSecond;
            long frames = (long)(seconds * _sampleRate);

            long writeCursor = _getWriteCursorFrames();
            bool monotonic = frames >= _lastFrames;
            // レイテンシ分のオーバーシュートは正常。1秒を超える逸脱のみ異常とみなす
            bool withinBounds = frames <= writeCursor + _sampleRate;

            if (!monotonic || !withinBounds)
            {
                _violationCount++;
                // ロックを保持しているので委譲する。直接書くと、デバッグモード有効時に
                // このロックを待つ相手（映像側と状態タイマー）までディスク I/O ぶん止まる
                Diagnostics.DiagnosticLog.WriteDeferred("pos",
                    $"sanity違反 count={_violationCount} monotonic={monotonic} withinBounds={withinBounds} frames={frames} lastFrames={_lastFrames} writeCursor={writeCursor}");
                if (_violationCount >= ViolationThreshold)
                {
                    _fallbackActive = true;
                    // **切替は常に残す。** ファイルにつき 1 度きり（以降は冒頭で短絡する）の
                    // 恒久的な劣化で、位置の精度が落ちるため利用者にも影響しうる。
                    // 診断ログ側に書くと既定運用では痕跡がゼロになり、
                    // 「位置表示がおかしい」の相談に対して何も手掛かりが無い状態になる。
                    // ロック内・vout スレッドから呼ばれるので必ず委譲する（GetPositionFrames の remarks）
                    Diagnostics.DiagnosticLog.WriteFatalDeferred("pos",
                        $"ハードウェア位置の検査に {ViolationThreshold} 回連続で失敗したため"
                        + $"フォールバックへ切り替えた（以降このファイルでは write cursor からの推定値を返す）"
                        + $" frames={frames} lastFrames={_lastFrames} writeCursor={writeCursor}");
                    return _fallback.GetPositionFrames();
                }
                return _lastFrames; // 直近の正常値を維持
            }

            _violationCount = 0;
            _lastFrames = frames;
            return frames;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _fallbackActive = false;
            _violationCount = 0;
            _lastFrames = 0;
            _fallback.Reset();
        }
    }
}
