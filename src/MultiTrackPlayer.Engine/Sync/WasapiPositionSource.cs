using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Sync;

/// <summary>
/// WasapiOut.GetPosition()（IWavePosition, OutputWaveFormat 単位のバイト位置）を
/// mixer サンプル軸（48kHz）のフレーム数へ写像する。
/// 単調性・write cursor との整合性を継続的に検査し、異常が連続したら FallbackPositionSource へ自動切替する。
/// </summary>
public sealed class WasapiPositionSource : IPlaybackPositionSource
{
    private const int ViolationThreshold = 5;

    private readonly IWavePosition _wavePosition;
    private readonly WaveFormat _outputFormat;
    private readonly int _sampleRate;
    private readonly Func<long> _getWriteCursorFrames;
    private readonly FallbackPositionSource _fallback;

    private bool _fallbackActive;
    private int _violationCount;
    private long _lastFrames;

    public bool IsFallbackActive => _fallbackActive;

    public WasapiPositionSource(IWavePosition wavePosition, WaveFormat outputFormat, int sampleRate,
        Func<long> getWriteCursorFrames, double latencySeconds)
    {
        _wavePosition = wavePosition;
        _outputFormat = outputFormat;
        _sampleRate = sampleRate;
        _getWriteCursorFrames = getWriteCursorFrames;
        _fallback = new FallbackPositionSource(getWriteCursorFrames, latencySeconds, sampleRate);
    }

    public long GetPositionFrames()
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
            if (_violationCount >= ViolationThreshold)
            {
                _fallbackActive = true;
                return _fallback.GetPositionFrames();
            }
            return _lastFrames; // 直近の正常値を維持
        }

        _violationCount = 0;
        _lastFrames = frames;
        return frames;
    }

    public void Reset()
    {
        _fallbackActive = false;
        _violationCount = 0;
        _lastFrames = 0;
        _fallback.Reset();
    }
}
