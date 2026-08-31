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

    /// <summary>
    /// <b>打刻の順序とファイル上の行順が食い違わないことの回帰テスト。</b>
    /// 同期経路（<c>Write</c>）は <c>FormatLine</c> をロック内で呼ぶ必要がある。外へ出すと
    /// 「打刻した順」と「書いた順」が入れ替わり、事象の順序を突き合わせる用途で
    /// <b>タイムスタンプが信用できなくなる</b>（委譲経路とは要求が逆で、あちらは書き込みが
    /// ずっと後になるので積む時点で打刻する）。
    /// </summary>
    /// <remarks>
    /// <b>偽陽性は出ない。</b> ロック内で打刻していれば逆転は原理的に起きない。
    /// 逆に窓を踏み外せば見逃す（打刻はミリ秒精度なので、境界を跨いだ瞬間だけが検出の機会）。
    /// <b>通ったことは保証ではなく反証の不在。</b>
    /// 反復回数は実測で決めた——<c>FormatLine</c> をロック外へ出すと 2 万行 × 2 スレッドで
    /// 3 回連続して検出できた（所要 500ms 程度）。
    /// </remarks>
    [Fact(DisplayName = "同時に書いても、行のタイムスタンプはファイル上で逆転しない")]
    public async Task Write_ConcurrentCallers_KeepsTimestampsInFileOrder()
    {
        const int linesPerThread = 20_000;

        Directory.CreateDirectory(_directory);
        DiagnosticLog.Enable(_directory);

        void Hammer(string category)
        {
            for (int i = 0; i < linesPerThread; i++) DiagnosticLog.Write(category, $"line {i}");
        }

        var other = Task.Run(() => Hammer("b"));
        Hammer("a");
        await other;

        // 閉じてから読む（書き込みが確定する）
        DiagnosticLog.Disable();

        string logPath = Assert.Single(Directory.GetFiles(_directory, "session-*.log"));
        string[] lines = File.ReadAllLines(logPath);

        // **このテストが同期経路で書いた行だけを見る。** 除外するものが 2 種類ある。
        // ①Enable / Disable が自分で書く「診断ログ開始」「診断ログ終了」
        // ②**他のテストクラスが書いた行。** DiagnosticLog は静的なグローバル状態で、
        //   xUnit はテストクラスを並列に走らせる。とくに委譲経路（WriteFatalDeferred）の行は
        //   **打刻より後に書かれるのが設計どおり**なので、混ざると必ず逆転として現れる
        //   （実際にこれで一度落ちた）。順序を保証するのは同期経路だけ
        var previous = TimeSpan.MinValue;
        int checkedLines = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[a] line ") && !lines[i].Contains("[b] line ")) continue;
            Assert.True(TimeSpan.TryParse(lines[i][..12], out var stamp), $"行 {i} の打刻が読めない: {lines[i]}");
            Assert.True(stamp >= previous,
                $"行 {i} で打刻が逆転した（前 {previous} → 今 {stamp}）: {lines[i]}");
            previous = stamp;
            checkedLines++;
        }

        // 書いた分がすべて残っていること（＝上のループが空回りしていないこと）を確かめる
        Assert.Equal(linesPerThread * 2, checkedLines);
    }
}
