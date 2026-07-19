namespace MultiTrackPlayer.Engine.Sync;

internal readonly record struct ClockSegment(long StartFrame, double SrcPtsSeconds, double Rate);

/// <summary>
/// mixer が WASAPI へ書き込んだサンプルフレーム数を軸とするセグメントマップ式クロック。
/// 実音声区間は Rate=再生速度、無音区間（シークギャップ／アンダーラン／プライミング待ち）は Rate=0 として記録し、
/// ハードウェア再生位置（フレーム数）をソース時刻へ写像する。
/// </summary>
public sealed class PlaybackClock
{
    private readonly object _lock = new();
    private readonly List<ClockSegment> _segments = new();
    private readonly int _sampleRate;
    private long _writeCursor;
    private double _currentRate = 1.0;
    private bool _seekPending;
    private double _seekTarget;
    private double? _pausedOverride;
    private double _lastReturnedPosition;

    public PlaybackClock(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        Reset();
    }

    public long WriteCursor { get { lock (_lock) return _writeCursor; } }

    public double? PausedOverride
    {
        get { lock (_lock) return _pausedOverride; }
        set { lock (_lock) _pausedOverride = value; }
    }

    /// <summary>Stop 時などに原点へ戻す。書込カーソルとセグメント履歴を全て破棄する。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _segments.Clear();
            _segments.Add(new ClockSegment(0, 0.0, 0.0));
            _writeCursor = 0;
            _currentRate = 1.0;
            _seekPending = false;
            _pausedOverride = null;
            _lastReturnedPosition = 0.0;
        }
    }

    /// <summary>
    /// シーク開始。以後 AnchorAt が呼ばれるまで PositionAt は target を返し続ける（gap 状態）。
    /// 現在の書込カーソルより先に予約されていた速度変更セグメント等は無効化する。
    /// </summary>
    public void BeginSeek(double targetSeconds)
    {
        lock (_lock)
        {
            _seekPending = true;
            _seekTarget = targetSeconds;
            _pausedOverride = null;
            ReplaceSegmentsFromLocked(_writeCursor, targetSeconds, 0.0);
        }
    }

    /// <summary>シーク後、最初の実音声サンプルが書かれた地点で呼ぶ。gap を解除し実区間を開始する。</summary>
    public void AnchorAt(long atFrame, double srcPtsSeconds)
    {
        lock (_lock)
        {
            ReplaceSegmentsFromLocked(atFrame, srcPtsSeconds, _currentRate);
            _seekPending = false;
        }
    }

    /// <summary>
    /// 再生速度変更。境界フレーム（= 変更時点の書込カーソル＋バッファ残量）から新レートを適用する。
    /// 境界より前は旧レートのまま連続性を保つ。
    /// </summary>
    public void SetSpeedAt(long boundaryFrame, double newRate)
    {
        lock (_lock)
        {
            double srcAtBoundary = PositionAtFrameLocked(boundaryFrame);
            ReplaceSegmentsFromLocked(boundaryFrame, srcAtBoundary, newRate);
            _currentRate = newRate;
        }
    }

    /// <summary>mixer が実音声を書いた分だけ書込カーソルを進める。</summary>
    public void OnAudioWritten(long frames)
    {
        lock (_lock) { _writeCursor += frames; }
    }

    /// <summary>mixer が無音（アンダーラン・priming 待ち等）を書いた分。クロックは進めない（Rate=0）。</summary>
    public void OnSilenceWritten(long frames)
    {
        lock (_lock)
        {
            if (_segments[^1].Rate != 0.0)
                ReplaceSegmentsFromLocked(_writeCursor, PositionAtFrameLocked(_writeCursor), 0.0);
            _writeCursor += frames;
        }
    }

    /// <summary>
    /// 指定フレーム位置（通常はハードウェア再生位置）のソース時刻を返す。
    /// PausedOverride 設定中・シーク保留中はそれぞれの固定値を返す。QPC 外挿ジッタ対策で単調非減少にクランプする。
    /// </summary>
    public double PositionAt(long hwFrames)
    {
        lock (_lock)
        {
            if (_pausedOverride is double p) return p;
            if (_seekPending) return _seekTarget;

            long clamped = Math.Min(hwFrames, _writeCursor);
            double raw = PositionAtFrameLocked(clamped);
            if (raw < _lastReturnedPosition) raw = _lastReturnedPosition;
            _lastReturnedPosition = raw;
            return raw;
        }
    }

    private double PositionAtFrameLocked(long frame)
    {
        var seg = FindSegmentLocked(frame);
        double elapsedSeconds = (frame - seg.StartFrame) / (double)_sampleRate;
        return seg.SrcPtsSeconds + elapsedSeconds * seg.Rate;
    }

    private ClockSegment FindSegmentLocked(long frame)
    {
        var result = _segments[0];
        foreach (var seg in _segments)
        {
            if (seg.StartFrame > frame) break;
            result = seg;
        }
        return result;
    }

    private void ReplaceSegmentsFromLocked(long fromFrame, double srcPtsSeconds, double rate)
    {
        _segments.RemoveAll(s => s.StartFrame >= fromFrame);
        _segments.Add(new ClockSegment(fromFrame, srcPtsSeconds, rate));
    }
}
