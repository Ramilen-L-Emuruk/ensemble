using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

/// <summary>
/// <see cref="IAudioOutput"/> の本番実装。WASAPI 共有モードの <see cref="WasapiOut"/> を包む。
/// </summary>
/// <remarks>
/// 全メンバーを内側へ委譲するだけの薄い包み。<see cref="PlaybackStopped"/> も
/// <c>add</c>/<c>remove</c> をそのまま通す——<b>利用側は送り主に依存しない</b>ので、
/// 上げ直して送り主を差し替える必要が無い（<see cref="IAudioOutput.PlaybackStopped"/> の注記）。
/// </remarks>
internal sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly WasapiOut _inner;
    private bool _disposed;

    /// <param name="latencyMs">WASAPI に要求するレイテンシ。呼び出し元が位置計算にも同じ値を使う。</param>
    /// <exception cref="Exception">
    /// 既定の再生デバイスが無い・Windows Audio サービス停止・RDP で音声リダイレクト無効といった
    /// 環境では <see cref="WasapiOut"/> の生成自体が失敗する。ここでは畳まず呼び出し元へ通す
    /// （<see cref="MediaEngine"/> 側が意味のあるメッセージへ変換する）。
    /// </exception>
    public WasapiAudioOutput(int latencyMs)
        => _inner = new WasapiOut(AudioClientShareMode.Shared, latencyMs);

    public WaveFormat OutputWaveFormat => _inner.OutputWaveFormat;

    public long GetPosition() => _inner.GetPosition();

    public void Init(IWaveProvider waveProvider) => _inner.Init(waveProvider);

    public void Play() => _inner.Play();

    public void Pause() => _inner.Pause();

    public void Stop() => _inner.Stop();

    /// <inheritdoc />
    public event EventHandler<StoppedEventArgs>? PlaybackStopped
    {
        add => _inner.PlaybackStopped += value;
        remove => _inner.PlaybackStopped -= value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }
}
