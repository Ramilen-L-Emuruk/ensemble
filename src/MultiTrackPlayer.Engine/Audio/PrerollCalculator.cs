namespace MultiTrackPlayer.Engine.Audio;

public enum PrerollActionKind
{
    /// <summary>フレーム全体がシーク目標より前。resample すら不要で丸ごと破棄する。</summary>
    DropAll,
    /// <summary>フレーム全体がシーク目標以降。そのまま採用する。</summary>
    KeepAll,
    /// <summary>フレームがシーク目標を跨ぐ。resample 後、先頭 SkipByteCount バイトを捨てる。</summary>
    SkipBytes
}

public readonly struct PrerollAction
{
    public PrerollActionKind Kind { get; }
    public int SkipByteCount { get; }

    private PrerollAction(PrerollActionKind kind, int skipByteCount)
    {
        Kind = kind;
        SkipByteCount = skipByteCount;
    }

    public static readonly PrerollAction Drop = new(PrerollActionKind.DropAll, 0);
    public static readonly PrerollAction Keep = new(PrerollActionKind.KeepAll, 0);
    public static PrerollAction Skip(int bytes) => new(PrerollActionKind.SkipBytes, bytes);
}

/// <summary>
/// シーク後のプリロール（AVSEEK_FLAG_BACKWARD で着地したキーフレーム以降、目標時刻より前の音声）を
/// サンプル精度で破棄するための判定ロジック。全て純粋関数で unsafe/FFmpeg 依存なし。
/// </summary>
public static class PrerollCalculator
{
    public static PrerollAction ComputeAction(
        double frameStartPtsSeconds,
        int nbSamples,
        int inSampleRate,
        double targetPtsSeconds,
        int effectiveOutSampleRate,
        int channels,
        int bytesPerSample)
    {
        if (inSampleRate <= 0 || nbSamples <= 0) return PrerollAction.Keep;

        double frameEndPtsSeconds = frameStartPtsSeconds + (double)nbSamples / inSampleRate;

        if (frameEndPtsSeconds <= targetPtsSeconds) return PrerollAction.Drop;
        if (frameStartPtsSeconds >= targetPtsSeconds) return PrerollAction.Keep;

        double skipSeconds = targetPtsSeconds - frameStartPtsSeconds;
        int skipOutSamples = (int)Math.Round(skipSeconds * effectiveOutSampleRate, MidpointRounding.AwayFromZero);
        int skipBytes = skipOutSamples * channels * bytesPerSample;

        return skipBytes <= 0 ? PrerollAction.Keep : PrerollAction.Skip(skipBytes);
    }
}
