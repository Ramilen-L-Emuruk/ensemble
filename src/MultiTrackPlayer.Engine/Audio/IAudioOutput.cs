using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

/// <summary>
/// ミキサーの出力を実際の再生デバイスへ流す音声出力。本番の実体は
/// <see cref="WasapiAudioOutput"/>（WASAPI 共有モード）。
/// </summary>
/// <remarks>
/// <b>切り出した理由は、テストから実パイプラインを動かすため。</b> このプロジェクトの不具合は
/// 大半が「音声出力が動いている間の並行処理」——待ち合わせの取りこぼし・滞留検出・
/// audio-master クロックの前進——に集中しているのに、<c>WasapiOut</c> を直接生成していたせいで、
/// それらを動かすには実デバイスと実アプリが必要だった。
/// <para>
/// <b>面をこれ以上広げないこと。</b> ここにあるのは <see cref="MediaEngine"/> が実際に使う
/// 7 つだけで、<c>IWavePlayer</c> の <c>Volume</c> / <c>PlaybackState</c> は含めていない
/// （音量はミキサー側が持ち、状態は <see cref="MediaEngine"/> 自身が持つ）。広げるほど
/// 差し替え実装が本物を真似る羽目になり、テストが本番から離れていく。
/// </para>
/// <para>
/// <see cref="IWavePosition"/> を継承しているのは、<c>Sync.WasapiPositionSource</c> が
/// もともとその形で位置を受け取るようにできているため（<c>GetPosition()</c> と
/// <c>OutputWaveFormat</c> の 2 つ）。ここで別の口を作ると同じ事実を 2 通りで表すことになる。
/// </para>
/// </remarks>
internal interface IAudioOutput : IWavePosition, IDisposable
{
    /// <summary>出力するミキサーを結び付ける。<see cref="Play"/> より先に 1 度だけ呼ぶ。</summary>
    void Init(IWaveProvider waveProvider);

    void Play();

    void Pause();

    void Stop();

    /// <summary>
    /// 再生が止まったときに発火する。<see cref="StoppedEventArgs.Exception"/> が非 null なら異常停止。
    /// </summary>
    /// <remarks>
    /// <b><c>sender</c> に何が載るかは実装依存で、利用側はそれに依存してはならない。</b>
    /// どの出力からの通知かを見分ける必要があるなら、<b>購読したときの実体を自分で覚えること</b>
    /// （<see cref="MediaEngine"/> はラムダで捕まえて渡している）。
    /// <para>
    /// <b>この形にしてある理由。</b> 以前は「実装は必ず自分自身を <c>sender</c> にすること」という
    /// 約束にしていたが、<b>守られたかを利用側から確かめる手立てが無い</b>。破られると
    /// 「いま自分が持っている出力からの通知か」の判定が常に外れ、
    /// <b>音声出力の異常停止が記録も通知もされなくなる</b>——しかも気づく手掛かりが残らない。
    /// 約束に頼るのをやめ、利用側が自分の参照で判定する形にした
    /// （<c>ensemble-review.md</c> §7「事実を観測しやすい代理値で置き換えない」）。
    /// </para>
    /// </remarks>
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
}
