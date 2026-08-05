using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.UI.Ipc;

/// <summary>
/// 二重起動を検知し、後発プロセスが受け取ったファイルパスを名前付きパイプ経由で
/// 既存プロセスへ引き渡すための単純な IPC。ミューテックスで最初の起動かどうかを判定し、
/// 最初の起動側は同名パイプでリッスンし続けて後発プロセスからのパスを受け取る。
/// </summary>
public static class SingleInstanceService
{
    private static readonly string PipeName = $"MultiTrackPlayer_Ipc_{Environment.UserName}";
    private static readonly string MutexName = $"Local\\MultiTrackPlayer_SingleInstance_{Environment.UserName}";

    private static Mutex? _mutex;

    /// <summary>このプロセスが最初の起動かどうかを判定し、最初の起動であれば多重起動防止用のミューテックスを保持する。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>ミューテックスを解放する。アプリ終了時に呼ぶ。</summary>
    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>既存インスタンスへファイルパスを送信する（自分が二重起動側だった場合に使う）。</summary>
    public static bool TrySendToRunningInstance(IReadOnlyList<string> filePaths, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            foreach (var path in filePaths)
                writer.WriteLine(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("ipc", $"既存インスタンスへの送信に失敗: {ex}");
            return false;
        }
    }

    /// <summary>既存インスタンス側で、後発プロセスからのファイルパスを待ち受け続ける。受信するたびに onFilesReceived を呼ぶ。</summary>
    public static void StartListening(Action<string[]> onFilesReceived, CancellationToken cancellationToken)
        => _ = Task.Run(() => ListenLoopAsync(onFilesReceived, cancellationToken), cancellationToken);

    private static async Task ListenLoopAsync(Action<string[]> onFilesReceived, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var files = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        files.Add(line);
                }

                if (files.Count > 0)
                    onFilesReceived(files.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("ipc", $"IPC 受信ループでエラー: {ex}");
            }
        }
    }
}
