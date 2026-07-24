using System.IO;
using System.Linq;

namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>
/// デバッグモード時のみ有効になる軽量診断ログ。%APPDATA%\MultiTrackPlayer\logs\session-*.log に書き出す。
/// シーク・フラッシュ・クロック錨・ミュート等の「イベント」だけを記録する（サンプル/フレーム単位では書かない）。
/// 起動のたびにファイルが増え続けないよう、直近 <see cref="MaxLogFileCount"/> 件を超える古いログは自動削除する。
/// </summary>
public static class DiagnosticLog
{
    private const int MaxLogFileCount = 10;
    private const string FileNamePattern = "session-*.log";

    private static readonly object Lock = new();
    private static StreamWriter? _writer;

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
            _writer.Dispose();
            _writer = null;
        }
    }

    public static void Write(string category, string message)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId,2}] [{category}] {message}");
        }
    }
}
