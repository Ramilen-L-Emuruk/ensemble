using System.IO;
using System.Linq;

namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>
/// デバッグモード時のみ有効になる軽量診断ログ。%APPDATA%\MultiTrackPlayer\logs\session-*.log に書き出す。
/// シーク・フラッシュ・クロック錨・ミュート等の「イベント」だけを記録する（サンプル/フレーム単位では書かない）。
/// 起動のたびにファイルが増え続けないよう、直近 <see cref="MaxLogFileCount"/> 件を超える古いログは自動削除する。
/// 致命的例外だけは <see cref="WriteFatal"/> でデバッグモードの設定に関わらず記録する。
/// </summary>
public static class DiagnosticLog
{
    private const int MaxLogFileCount = 10;
    private const string FileNamePattern = "session-*.log";
    private const string FatalFileName = "fatal.log";
    // fatal ログは無効時でも追記するため、放置すると際限なく育つ。この大きさを超えたら作り直す
    private const long MaxFatalFileBytes = 1 * 1024 * 1024;

    private static readonly object Lock = new();
    private static StreamWriter? _writer;

    /// <summary>ログの既定の出力先。<see cref="WriteFatal"/> のフォールバック先も兼ねる。</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MultiTrackPlayer", "logs");

    public static bool Enabled { get; private set; }

    public static void Enable(string directory)
    {
        lock (Lock)
        {
            if (_writer != null) return;
            try
            {
                Directory.CreateDirectory(directory);
                DeleteOldLogs(directory);
                string path = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                // FileShare.Read: アプリ実行中でも外部からログを閲覧できるようにする
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                // AutoFlush=true はロック保持中にディスク I/O を行うことを意味するが、
                // バッファリングするとクラッシュ直前の数行（＝最も知りたい部分）が失われるため
                // 診断ログの目的と衝突する。ここでは確実に残ることを優先する
                _writer = new StreamWriter(stream) { AutoFlush = true };
                Enabled = true;
                Write("log", "診断ログ開始");
            }
            catch
            {
                // ログが書けない環境でも再生機能は止めない
                _writer = null;
                Enabled = false;
            }
        }
    }

    // ファイル名が session-yyyyMMdd-HHmmss.log 形式のため、文字列昇順ソート = 時系列昇順ソートになる。
    private static void DeleteOldLogs(string directory)
    {
        try
        {
            var files = Directory.GetFiles(directory, FileNamePattern).OrderBy(f => f).ToList();
            int deleteCount = files.Count - (MaxLogFileCount - 1);
            for (int i = 0; i < deleteCount; i++)
            {
                try { File.Delete(files[i]); }
                catch { /* 使用中などで削除できなくても継続する */ }
            }
        }
        catch
        {
            // 一覧取得に失敗しても新規ログの書き込みは継続する
        }
    }

    public static void Disable()
    {
        lock (Lock)
        {
            if (_writer == null) return;
            Write("log", "診断ログ終了");
            Enabled = false;
            try { _writer.Dispose(); }
            catch { /* 終了時の書き出し失敗でアプリを巻き添えにしない */ }
            _writer = null;
        }
    }

    public static void Write(string category, string message)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            WriteLineToSessionLog(FormatLine(category, message));
        }
    }

    /// <summary>fatal.log への追記が複数プロセスで競合しないようにするための名前。</summary>
    private const string FatalFileMutexName = @"Local\MultiTrackPlayer_FatalLog";
    private static readonly TimeSpan FatalFileLockTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FatalFileRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 致命的例外など、失われると原因究明が不可能になる事象を記録する。
    /// デバッグモードが無効でも記録する点が <see cref="Write"/> との違い。
    /// セッションログが開いていればそちらへ、無ければ <see cref="DefaultDirectory"/> の fatal.log へ追記する。
    /// </summary>
    public static void WriteFatal(string category, string message)
    {
        string line = FormatLine(category, message);
        bool writtenToSession;
        lock (Lock)
        {
            writtenToSession = WriteLineToSessionLog(line);
        }
        // セッションログ側の書き込みが失敗した場合も記録が消えないよう fatal.log へ回す。
        // 「最も知りたい瞬間のログだけ失われる」のを避けるため、ここは必ず二段構えにする。
        // プロセス間ミューテックスの待機を伴うので、Lock は必ず解放してから呼ぶ
        // （デバッグモード無効時はこちらが既定経路になるため、Lock を握ったままだと
        //   デバッグモード切替などが最大 500ms ブロックされる）
        if (!writtenToSession) AppendToFatalFile(line);
    }

    private static string FormatLine(string category, string message)
        => $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId,2}] [{category}] {message}";

    /// <returns>セッションログへ書けた場合 true。</returns>
    private static bool WriteLineToSessionLog(string line)
    {
        if (_writer == null) return false;
        try
        {
            _writer.WriteLine(line);
            return true;
        }
        catch
        {
            // ディスクフル・ハンドル異常等で書けなくなっても再生機能を巻き添えにしない。
            // 以後の呼び出しで例外を繰り返さないよう、セッションログ自体を閉じる
            Enabled = false;
            try { _writer?.Dispose(); }
            catch { /* 破棄にも失敗したら参照を捨てるだけにする */ }
            _writer = null;
            return false;
        }
    }

    private static void AppendToFatalFile(string line)
    {
        // 二重起動していると両プロセスから同時に追記されて片方が失われる。
        // ただし異常終了の処理を長く止めるわけにもいかないので、取れなければ諦めて書く
        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            mutex = new Mutex(false, FatalFileMutexName);
            try { acquired = mutex.WaitOne(FatalFileLockTimeout); }
            catch (AbandonedMutexException) { acquired = true; } // 直前の保持プロセスが落ちた場合

            Directory.CreateDirectory(DefaultDirectory);
            string path = Path.Combine(DefaultDirectory, FatalFileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxFatalFileBytes)
                File.Delete(path);

            // ミューテックスを取れないまま書き込みが競合すると IOException になり、
            // セッションログにも書けていないこの行が完全に失われる。間を置いて 1 度だけ再試行する
            if (!TryAppendLine(path, line) && !acquired)
            {
                Thread.Sleep(FatalFileRetryDelay);
                TryAppendLine(path, line);
            }
        }
        catch
        {
            // ログが書けない環境でも、異常終了そのものの処理は妨げない
        }
        finally
        {
            if (acquired)
            {
                try { mutex!.ReleaseMutex(); }
                catch { /* 解放できなくても終了処理は続ける */ }
            }
            mutex?.Dispose();
        }
    }

    /// <returns>書き込めた場合 true。</returns>
    private static bool TryAppendLine(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
