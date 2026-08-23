using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.UI.Ipc;

/// <summary>
/// 二重起動を検知し、後発プロセスが受け取ったファイルパスを名前付きパイプ経由で
/// 既存プロセスへ引き渡すための単純な IPC。ミューテックスで最初の起動かどうかを判定し、
/// 最初の起動側は同名パイプでリッスンし続けて後発プロセスからのパスを受け取る。
/// パイプ名は同一マシンの全プロセスから見える名前空間にあるため、受信したパスは
/// 信頼できない入力として扱い <see cref="IsAcceptablePath"/> で検証してから利用する。
/// </summary>
public static class SingleInstanceService
{
    private static readonly string PipeName = $"MultiTrackPlayer_Ipc_{Environment.UserName}";
    private static readonly string MutexName = $"Local\\MultiTrackPlayer_SingleInstance_{Environment.UserName}";

    /// <summary>1 回の送信で受け付けるファイル数の上限（信頼できない入力によるメモリ枯渇を防ぐ）。</summary>
    private const int MaxReceivedFileCount = 1000;
    /// <summary>1 行あたりに受け付ける文字数の上限。Windows の拡張パス長を大きく上回る値にしてある。</summary>
    private const int MaxPathLength = 4096;
    /// <summary>パイプ生成に失敗したときの再試行間隔。連続失敗時のビジーループを防ぐ。</summary>
    private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromMilliseconds(500);

    private static Mutex? _mutex;
    // initiallyOwned:true でも既存の Mutex がある場合は所有権を得られない。
    // 所有していない Mutex に ReleaseMutex すると例外になるため、所有の有無を覚えておく
    private static bool _ownsMutex;

    /// <summary>このプロセスが最初の起動かどうかを判定し、最初の起動であれば多重起動防止用のミューテックスを保持する。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            // 所有権を得られなかったハンドルは保持し続ける意味がないため、この場で閉じる
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    /// <summary>ミューテックスを解放する。アプリ終了時に呼ぶ。</summary>
    public static void Release()
    {
        var mutex = _mutex;
        if (mutex == null) return;
        try
        {
            if (_ownsMutex) mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            // 所有権の追跡が何らかの理由でずれていても、終了処理そのものは止めない
            DiagnosticLog.Write("ipc", $"ミューテックスの解放に失敗: {ex}");
        }
        finally
        {
            mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
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
    /// <returns>待受ループの Task。アプリ終了時に完了を待つために使う。</returns>
    public static Task StartListening(Action<string[]> onFilesReceived, CancellationToken cancellationToken)
        => Task.Run(() => ListenLoopAsync(onFilesReceived, cancellationToken), cancellationToken);

    private static async Task ListenLoopAsync(Action<string[]> onFilesReceived, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 既定 DACL は Everyone に読み取りを許すため、明示的に現在のユーザーだけに絞る。
                // （名前付きパイプはセッション分離の対象外で、共有 PC・RDP では別ユーザーから見える）
                using var server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, CreateCurrentUserOnlySecurity());
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var files = await ReadPathsAsync(server, cancellationToken).ConfigureAwait(false);
                if (files.Length > 0)
                    onFilesReceived(files);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("ipc", $"IPC 受信ループでエラー: {ex}");
                // パイプ名を他プロセスに先取りされている等、生成自体が失敗し続ける状況では
                // 遅延なしに再試行すると CPU を食い潰す。一定間隔を空けてから再試行する
                try { await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static PipeSecurity CreateCurrentUserOnlySecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        IdentityReference owner = (IdentityReference?)identity.User ?? new NTAccount(Environment.UserName);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static async Task<string[]> ReadPathsAsync(Stream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, Encoding.UTF8);
        var files = new List<string>();
        string? line;
        while (files.Count < MaxReceivedFileCount
               && (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxPathLength) continue;
            if (!IsLocalDrivePath(line))
            {
                DiagnosticLog.WriteFatal("ipc", $"ローカルドライブ上のパスではないため無視: {line}");
                continue;
            }
            files.Add(line);
        }
        return files.ToArray();
    }

    /// <summary>
    /// 受け取ったパスがローカルドライブ上を指しているかを判定する。
    /// 送信元はパイプ名さえ知っていれば任意のプロセスになりうるため、
    /// ここを通さずに <see cref="File.Exists"/> へ渡してはならない
    /// （リモートを指すパスは存在確認の時点で SMB 認証が走り、資格情報が外部ホストへ送出されうる）。
    /// </summary>
    /// <remarks>
    /// UNC を表す記法は <c>\\host\share</c> のほかにも <c>//host/share</c>・<c>/\host\share</c>・
    /// <c>\\?\GLOBALROOT\Device\Mup\...</c> など複数あり、拒否リストでは取りこぼす。
    /// そのため「正規化した結果がドライブレター始まりであること」だけを許す許可リスト方式にする。
    /// 対応形式かどうかはここでは判定しない（D&amp;D やファイルダイアログ経由と挙動を揃えるため、
    /// 拡張子の可否は FFmpeg に委ねる。ここで絞ると同じファイルが経路によって開けなくなる）。
    /// </remarks>
    private static bool IsLocalDrivePath(string path)
    {
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; } // 解決できない表記は受け付けない

        // "C:\..." の形だけを通す。UNC・デバイス名前空間・拡張パス記法はここで落ちる
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':'
            || full[2] != Path.DirectorySeparatorChar) return false;

        // マップ済みネットワークドライブ（Z: が UNC 共有を指す等）は文字列上ローカルパスと
        // 区別がつかないため、ドライブ種別で確認する
        try
        {
            if (new DriveInfo(full[..1]).DriveType == DriveType.Network) return false;
        }
        catch
        {
            // 種別を判定できないドライブは、ここでは弾かず後段の File.Exists に委ねる
        }

        // "C:\CON" のように末尾が予約デバイス名だと、Win32 はディレクトリ部分を無視して
        // レガシーデバイスへ解決してしまう（拡張子の有無を問わない）。ファイルとして開く意図から外れる
        string stem = Path.GetFileNameWithoutExtension(full);
        return !ReservedDeviceNames.Contains(stem);
    }

    /// <summary>Win32 が特別扱いするレガシーデバイス名。</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
