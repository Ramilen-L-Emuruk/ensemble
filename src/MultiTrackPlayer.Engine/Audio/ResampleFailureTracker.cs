namespace MultiTrackPlayer.Engine.Audio;

/// <summary>
/// トラックごとのリサンプル連続失敗回数を数え、「このトラックはもう音声を出せない」と
/// 判定する。FFmpeg 依存を持たない純ロジックのためユニットテストできる。
///
/// <para>
/// 判定が必要な理由: リサンプルに失敗し続けるトラックは出力ゼロのまま EOF にもならず、
/// <see cref="MultiTrackMixer"/> の共通利用可能量（全トラックの最小残量）を 0 に固定してしまう。
/// 結果として健全な他トラックまで無音になり、クロックも進まず映像まで止まる。
/// 破損データによる散発的な失敗でトラックを切り離してしまわないよう、
/// 連続失敗が閾値に達してから畳む。
/// </para>
/// <para>
/// 範囲外の添字は例外にせず黙って無視する。これらは音声デコードスレッドの中で毎フレーム
/// 呼ばれるため、ここで例外を投げるとスレッドごと停止して全トラックが無音になる
/// （＝このクラスが防ごうとしている症状そのものを引き起こす）。意図的に fail-fast にしない。
/// </para>
/// </summary>
public sealed class ResampleFailureTracker
{
    /// <summary>
    /// 既定の連続失敗許容回数。音声 1 フレームは概ね 1024 サンプル（48kHz で約 21ms）なので、
    /// 50 回連続の失敗は 1 秒強ぶんのデータを一切出力できていないことを意味する。
    /// </summary>
    public const int DefaultThreshold = 50;

    private readonly int[] _consecutiveFailures;

    /// <summary>このトラックを畳むと判定する連続失敗回数。</summary>
    public int Threshold { get; }

    public int TrackCount => _consecutiveFailures.Length;

    /// <exception cref="ArgumentOutOfRangeException">trackCount が負、または threshold が 1 未満の場合。</exception>
    public ResampleFailureTracker(int trackCount, int threshold = DefaultThreshold)
    {
        if (trackCount < 0)
            throw new ArgumentOutOfRangeException(nameof(trackCount), trackCount, "トラック数に負値は指定できない");
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "閾値は 1 以上でなければならない");

        _consecutiveFailures = new int[trackCount];
        Threshold = threshold;
    }

    /// <summary>リサンプル失敗を 1 回記録する。範囲外の添字は無視する。</summary>
    /// <returns>
    /// 連続失敗が閾値に達し、このトラックを畳むべきになった<b>その瞬間だけ</b> true。
    /// 閾値を超えて呼び続けても false のままなので、呼び出し側は畳む処理の重複を気にせず済む。
    /// </returns>
    public bool RecordFailure(int trackIndex)
    {
        if (!IsInRange(trackIndex)) return false;
        _consecutiveFailures[trackIndex]++;
        return _consecutiveFailures[trackIndex] == Threshold;
    }

    /// <summary>リサンプル成功を記録し、連続失敗の数え直しを始める。範囲外の添字は無視する。</summary>
    public void RecordSuccess(int trackIndex)
    {
        if (!IsInRange(trackIndex)) return;
        _consecutiveFailures[trackIndex] = 0;
    }

    /// <summary>シークで全トラックのバッファを捨てたときなど、判定をやり直す。</summary>
    public void Reset() => Array.Clear(_consecutiveFailures);

    /// <summary>現在の連続失敗回数（範囲外の添字は 0）。</summary>
    public int GetConsecutiveFailures(int trackIndex) =>
        IsInRange(trackIndex) ? _consecutiveFailures[trackIndex] : 0;

    private bool IsInRange(int trackIndex) => trackIndex >= 0 && trackIndex < _consecutiveFailures.Length;
}
