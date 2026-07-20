using System.IO;

namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>
/// デバッグモード時のみ有効になる軽量診断ログ。%APPDATA%\MultiTrackPlayer\logs\session-*.log に書き出す。
/// シーク・フラッシュ・クロック錨・ミュート等の「イベント」だけを記録する（サンプル/フレーム単位では書かない）。
/// </summary>
public static class DiagnosticLog
{
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
