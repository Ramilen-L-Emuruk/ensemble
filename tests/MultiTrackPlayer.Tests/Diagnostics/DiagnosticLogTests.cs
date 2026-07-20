using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Diagnostics;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory;

    public DiagnosticLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "MultiTrackPlayerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        DiagnosticLog.Disable();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Enable_DeletesOldestFiles_WhenExistingLogsExceedLimit()
    {
        Directory.CreateDirectory(_directory);
        // ファイル名が時系列順にソートされる前提で、あらかじめ15件の古いログを用意する
        for (int i = 0; i < 15; i++)
        {
            string name = $"session-20260101-{i:D2}0000.log";
            File.WriteAllText(Path.Combine(_directory, name), "dummy");
        }

        DiagnosticLog.Enable(_directory);

        string[] files = Directory.GetFiles(_directory, "session-*.log");
        Assert.True(files.Length <= 10, $"expected at most 10 files, but found {files.Length}");

        // 最も新しかった既存ログ (index 14) は削除されずに残っているはず
        Assert.Contains(files, f => f.Contains("session-20260101-140000.log"));
        // 最も古かった既存ログ (index 0) は削除されているはず
        Assert.DoesNotContain(files, f => f.Contains("session-20260101-000000.log"));
    }
}
