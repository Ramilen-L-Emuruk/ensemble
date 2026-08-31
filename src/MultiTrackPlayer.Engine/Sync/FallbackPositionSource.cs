using System.Diagnostics;

namespace MultiTrackPlayer.Engine.Sync;

/// <summary>
/// WasapiPositionSource の異常時フォールバック。write cursor からレイテンシを差し引いた値を基準に、
/// write cursor が変化しない間は経過時間ぶん Stopwatch で外挿し、滑らかな値を返す。
/// </summary>
/// <remarks>
/// <b>このクラスは自分では同期しない。</b> 到達経路は <see cref="WasapiPositionSource"/> の
/// ロック内だけで（外から直に構築している箇所は無い）、状態
/// （<see cref="_baseCursor"/> / <see cref="_baseFrames"/> / <see cref="_stopwatch"/>）は
/// そこで守られている。<b><c>Stopwatch</c> はスレッドセーフではない</b>ので、
/// 直接使う経路を作るならこちらにも同じ守りが必要。
/// </remarks>
public sealed class FallbackPositionSource : IPlaybackPositionSource
{
    private readonly Func<long> _getWriteCursorFrames;
    private readonly long _latencyFrames;
    private readonly int _sampleRate;
    private readonly Stopwatch _stopwatch = new();

    private long _baseFrames;
    private long _baseCursor = -1;

    public FallbackPositionSource(Func<long> getWriteCursorFrames, double latencySeconds, int sampleRate)
    {
        _getWriteCursorFrames = getWriteCursorFrames;
        _sampleRate = sampleRate;
        _latencyFrames = (long)(latencySeconds * sampleRate);
        _stopwatch.Start();
    }

    public long GetPositionFrames()
    {
        long cursor = _getWriteCursorFrames();
        long estimate = Math.Max(0, cursor - _latencyFrames);

        if (cursor != _baseCursor)
        {
            _baseCursor = cursor;
            _baseFrames = estimate;
            _stopwatch.Restart();
            return estimate;
        }

        // write cursor が変化していない間（バッファ供給の谷間）は経過時間ぶん外挿して滑らかに進める。
        // ただし write cursor 自体は超えない。
        long extrapolated = _baseFrames + (long)(_stopwatch.Elapsed.TotalSeconds * _sampleRate);
        return Math.Min(extrapolated, cursor);
    }

    public void Reset()
    {
        _baseCursor = -1;
        _baseFrames = 0;
        _stopwatch.Restart();
    }
}
