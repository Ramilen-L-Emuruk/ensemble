namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 統合テストで使うメディアファイルを、テストクラス単位で 1 度だけ作って共有する。
/// </summary>
/// <remarks>
/// 生成は数百 ms かかるため、<c>IClassFixture</c> で使い回す。<b>置き場はテンポラリで、
/// テストごとに違うディレクトリ</b>——テストランナーはクラス単位で並列に走るので、
/// 固定名にすると別クラスと同じファイルを掴む。
/// </remarks>
public sealed class TestMediaFixture : IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _created = [];

    public TestMediaFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "MultiTrackPlayer.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>映像＋音声 3 トラック・3 秒。マルチトラックの取り扱いを試すもの。</summary>
    public string ThreeTracks => GetOrCreate("three", TimeSpan.FromSeconds(3), audioTrackCount: 3);

    /// <summary>音声のみ 1 トラック・3 秒。映像側の経路を通らない場合を試すもの。</summary>
    public string AudioOnly =>
        GetOrCreate("audio-only", TimeSpan.FromSeconds(3), audioTrackCount: 1, includeVideo: false);

    private string GetOrCreate(
        string key, TimeSpan duration, int audioTrackCount, bool includeVideo = true)
    {
        lock (_gate)
        {
            if (_created.TryGetValue(key, out string? existing)) return existing;

            string path = Path.Combine(_root, $"{key}.mp4");
            TestMediaFactory.CreateMp4(path, duration, audioTrackCount, includeVideo: includeVideo);
            _created[key] = path;
            return path;
        }
    }

    public void Dispose()
    {
        // 消せなくてもテストの失敗にはしない。テンポラリなので OS がいずれ回収する
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
