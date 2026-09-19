using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 偽の音声出力を差し込んだ <see cref="MediaEngine"/> と、その後始末の検査を一体で持つ足場。
/// </summary>
/// <remarks>
/// <b>エンジンと偽の出力の破棄順を、書き方ではなく構造で保証するためにある。</b>
/// 検査（読み出しスレッドが止まったか）を行うのは
/// <see cref="FakeAudioOutputProvider.Dispose"/> だが、それは<b>エンジンを破棄した後</b>でなければ
/// 意味が無い——エンジンの後始末が偽の出力を破棄して初めて停止の成否が分かる。
/// <para>
/// 以前はテスト側で <c>using var provider = ...;</c> を <c>using var engine = ...;</c> より先に
/// 宣言する規約にしていたが、<b>順序を間違えても・<c>using</c> を忘れてもコンパイルは通り、
/// テストは緑のまま通る</b>——検査が黙って行われなくなる。<b>守られたかを確かめられない約束</b>は
/// この変更で 1 度外しているので（<c>IAudioOutput.PlaybackStopped</c> の注記）、ここも同じ形にした。
/// </para>
/// <para>使い方は <c>using</c> 1 つだけ。</para>
/// <code>
/// using var harness = new MediaEngineHarness();
/// harness.Engine.Open(path);
/// harness.Engine.Play();
/// harness.Output.AdvanceMs(20);
/// </code>
/// </remarks>
internal sealed class MediaEngineHarness : IDisposable
{
    private readonly FakeAudioOutputProvider _provider = new();

    /// <param name="timings">
    /// 滞留検出の閾値と猶予。<c>null</c> なら本番の値のまま。<b>滞留を試すテストだけが渡す</b>——
    /// 本番の値は 3〜5 秒あり、そのまま待つとテスト 1 本で 10 秒級になる。
    /// </param>
    public MediaEngineHarness(StallTimings? timings = null)
        => Engine = new MediaEngine(_provider.Create, timings);

    /// <summary>試験対象のエンジン。音声出力だけが偽物で、他は本番と同じ構成で動く。</summary>
    public MediaEngine Engine { get; }

    /// <summary>
    /// いまエンジンが使っている音声出力。<b><see cref="IMediaEngine.Open"/> を呼んだ後に取り出す</b>
    /// （出力はファイルを開くたびに作り直される）。
    /// </summary>
    public FakeAudioOutput Output => _provider.Current;

    /// <summary>これまでに作られた音声出力を、作られた順に返す。ファイル切替の検証で使う。</summary>
    public IReadOnlyList<FakeAudioOutput> CreatedOutputs => _provider.Created;

    /// <remarks>
    /// <b>エンジンを先に破棄する。</b> 逆にすると、まだ止まっていない読み出しスレッドを
    /// 見逃したまま検査が終わる。
    /// </remarks>
    public void Dispose()
    {
        Engine.Dispose();
        _provider.Dispose();
    }
}
