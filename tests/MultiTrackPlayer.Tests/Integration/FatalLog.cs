using System.Diagnostics;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 常に残る側のログ（<c>fatal.log</c>）をテストから読む。
/// </summary>
/// <remarks>
/// <b>読む側が <see cref="ProcessWideStateCollection"/> に属していることが前提。</b>
/// <c>WriteFatal</c> はセッションログが開いていればそちらへ書くため、
/// <c>DiagnosticLog.Enable</c> を呼ぶクラスと並列に走ると記録がここへ来ない。
/// <para>
/// <b>このファイルは追記式で、消えない。</b> 実行中のアプリ・過去の実行・他プロセスの行が
/// 積み上がっているので、<b>「含まれている」を素で見てはいけない</b>——
/// <b>前回の実行で自分が書いた行に当たる</b>。実際にそれで、検出器を丸ごと止めても
/// 緑のまま通るテストを書いた。探すときは <see cref="Bookmark"/> で位置を取り、
/// <b>それより後に追記された分だけ</b>を見ること。
/// </para>
/// </remarks>
internal static class FatalLog
{
    /// <summary>
    /// ファイル名を直接書いているのは、<c>DiagnosticLog</c> 側が非公開の定数として持っているため。
    /// <b>変えるときは両方直すこと。</b>
    /// </summary>
    private const string FileName = "fatal.log";

    private static string Path => System.IO.Path.Combine(DiagnosticLog.DefaultDirectory, FileName);

    /// <summary>全文を読む。ファイルが無ければテストを失敗させる。</summary>
    /// <remarks>
    /// <b>自分の実行に固有の文字列を探す場合にだけ使うこと</b>（呼び出しごとに変える目印を
    /// 記録へ載せられる経路）。そうでなければ <see cref="WaitForContains"/> を使う。
    /// </remarks>
    public static string ReadAll()
    {
        string path = Path;
        if (!File.Exists(path))
            Assert.Fail($"常に残る側のログが作られていない path={path}");

        return ReadFrom(path, 0);
    }

    /// <summary>
    /// いまのファイル末尾の位置を覚える。<b>記録を待つ操作の前に取る。</b>
    /// </summary>
    /// <remarks>
    /// ファイルが無い場合は 0。<b>戻り値を「行数」ではなくバイト位置として扱っている</b>のは、
    /// 追記の途中（改行が来る前）に読んでも取りこぼさないようにするため。
    /// </remarks>
    public static long Bookmark()
    {
        string path = Path;
        if (!File.Exists(path)) return 0;
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // 他プロセスが書き換えている最中なら 0 として扱う。全文を見ることになるので
            // 取りこぼしはしない（過去の行を拾う方向へ倒れるが、それは失敗を見逃す側ではない
            // ——この後の assert が甘くなるだけで、ここで例外にすると無関係な失敗になる）
            return 0;
        }
    }

    /// <summary>
    /// <paramref name="bookmark"/> より後に <paramref name="needle"/> が追記されるまで待つ。
    /// 現れなければ失敗させる。
    /// </summary>
    /// <remarks>
    /// <b>待つ必要があるのは、滞留の記録がスレッドプールへ逃がされている</b>ため
    /// （<c>MediaEngine.QueueFatalRecord</c>。状態タイマーのコールバック内でファイル I/O と
    /// プロセス間ロックを取ると、利用者の操作がその待ちへ巻き込まれる）。
    /// <b>書かれる順も時刻も約束されていない</b>ので、1 度読んで無ければ失敗、では取りこぼす。
    /// </remarks>
    public static void WaitForContains(long bookmark, string needle, string what)
    {
        var elapsed = Stopwatch.StartNew();
        string appended = string.Empty;
        while (elapsed.Elapsed < Timeout)
        {
            string path = Path;
            if (File.Exists(path))
            {
                appended = ReadFrom(path, bookmark);
                if (appended.Contains(needle, StringComparison.Ordinal)) return;
            }
            Thread.Sleep(20);
        }

        Assert.Fail(
            $"{what} が {Timeout.TotalSeconds} 秒で記録されなかった needle={needle}"
            + $"{Environment.NewLine}この間に追記された分:{Environment.NewLine}{Tail(appended)}");
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <paramref name="from"/> バイト目から末尾までを読む。
    /// </summary>
    /// <remarks>
    /// <para>実行中のアプリや他プロセスが追記していることがあるので共有して開く。</para>
    /// <para>
    /// <b>いまの長さが <paramref name="from"/> より短ければ先頭から読む。</b>
    /// <c>DiagnosticLog</c> は 1MB を超えたファイルを<b>削除して新しく書き始める</b>ため
    /// （切り詰めではない）、位置を覚えた後にファイルが短くなりうる——そのときは
    /// 覚えた位置に意味が無い。
    /// </para>
    /// <para>
    /// <b><paramref name="from"/> が長さと同じときに seek を省いてはいけない。</b>
    /// それは「まだ何も追記されていない」という最も普通の状態で、省くと全文を読んで
    /// <b>過去の行を「いま書かれた」と誤認する</b>。この取り違えで、検出器を丸ごと止めても
    /// 緑のまま通るテストが 2 度できた（1 度目は位置を見ていなかったこと、2 度目がこれ）。
    /// </para>
    /// </remarks>
    private static string ReadFrom(string path, long from)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = from <= stream.Length ? from : 0;
        if (start > 0) stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>失敗時の説明に載せる末尾。全文だと他プロセスの行で埋まって読めない。</summary>
    private static string Tail(string content)
    {
        string[] lines = content.Split('\n');
        return string.Join(Environment.NewLine, lines[Math.Max(0, lines.Length - 15)..]);
    }
}
