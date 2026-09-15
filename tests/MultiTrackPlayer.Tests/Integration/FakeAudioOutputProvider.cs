using MultiTrackPlayer.Engine.Audio;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// <see cref="FakeAudioOutput"/> を <c>MediaEngine</c> へ渡すための入れ物。
/// </summary>
/// <remarks>
/// <c>MediaEngine</c> はファイルを開くたびに音声出力を作り直すため、テストからは
/// 「いま使われている出力」を後から取り出せる必要がある。<b>作った順に全部覚えておく</b>のは、
/// ファイル切替の試験で「前の出力が破棄されたか」も確かめるため。
/// <para>
/// <b>破棄で「読み出しスレッドが止まらなかった」ことを表明する。</b> 表明をここに置くのは、
/// <see cref="FakeAudioOutput"/> 自身の <c>Dispose</c> が <c>MediaEngine</c> の後始末の途中で
/// 呼ばれるため——あちらで投げると<b>本番の後始末が丸ごと飛ぶ</b>
/// （<see cref="FakeAudioOutput.Dispose"/> の注記）。この入れ物はテストが所有するので、
/// 破棄で投げても本番の経路を壊さない。
/// </para>
/// <para>
/// <b>破棄の順序は <see cref="MediaEngineHarness"/> が持っている。</b> この入れ物を
/// テストから直接 <c>using</c> しないこと——エンジンより先に破棄すると、まだ止まっていない
/// 読み出しスレッドを見逃す。<b>順序をテストの書き方に委ねると、間違えても忘れても
/// コンパイルは通り、検査だけが黙って行われなくなる。</b>
/// </para>
/// </remarks>
internal sealed class FakeAudioOutputProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly List<FakeAudioOutput> _created = [];
    private bool _disposed;

    /// <summary><c>MediaEngine</c> のコンストラクタへ渡すファクトリ。</summary>
    public IAudioOutput Create(int latencyMs)
    {
        var output = new FakeAudioOutput(latencyMs);
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FakeAudioOutputProvider));
            _created.Add(output);
        }
        return output;
    }

    /// <summary>直近に作られた出力。<c>Open</c> の後に取り出す。</summary>
    public FakeAudioOutput Current
    {
        get
        {
            lock (_gate)
            {
                return _created.Count > 0
                    ? _created[^1]
                    : throw new InvalidOperationException(
                        "まだ音声出力が作られていない（Open を呼ぶ前に取り出している）");
            }
        }
    }

    /// <summary>これまでに作られた出力を、作られた順に返す。</summary>
    public IReadOnlyList<FakeAudioOutput> Created
    {
        get { lock (_gate) return _created.ToArray(); }
    }

    /// <remarks>
    /// <b>止まらなかった読み出しスレッドをここで表明する。</b> 黙って見逃すと、
    /// <b>この足場が再現したい症状（スレッドが永久に戻らない）だけが検知されない</b>ことになり、
    /// しかも残ったスレッドが破棄済みのパイプラインを触り続けて後続の無関係なテストが
    /// 原因不明で不安定になる。
    /// <para>
    /// テスト本体の例外が伝播している最中にここで投げると<b>元の例外を差し替える</b>点は
    /// 承知のうえ。それでも表明するのは、黙ると後続まで壊れるのに対し、隠れるのは
    /// この 1 件の一次原因にとどまるため。理由の文面には実際に判明した内容を載せる。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        FakeAudioOutput[] outputs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            outputs = _created.ToArray();
        }

        string[] stuck = outputs
            .Select(o => o.StuckReason)
            .Where(reason => reason != null)
            .Select(reason => reason!)
            .ToArray();

        if (stuck.Length > 0)
        {
            throw new InvalidOperationException(
                "偽の音声出力の読み出しスレッドに問題が残っている"
                + $"（{stuck.Length} 件）。テスト本体で先に起きた失敗がこの例外に"
                + $"差し替えられていることがある:{Environment.NewLine}"
                + string.Join(Environment.NewLine, stuck));
        }
    }
}
