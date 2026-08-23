using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.UI.Ipc;

namespace MultiTrackPlayer.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>起動時のコマンドライン引数（動画ファイルパス）。MainWindow が Loaded 時に参照する。</summary>
        public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

        /// <summary>既存インスタンスがパイプを開くまでの待ち時間。起動直後に二重起動された場合を見込んで長めに取る。</summary>
        private static readonly TimeSpan SendToRunningInstanceTimeout = TimeSpan.FromSeconds(10);
        /// <summary>終了時に IPC 受信ループの停止を待つ時間。</summary>
        private static readonly TimeSpan IpcShutdownTimeout = TimeSpan.FromSeconds(2);

        private readonly CancellationTokenSource _ipcListenCts = new();
        private Task? _ipcListenTask;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            StartupArgs = e.Args;

            // 単一起動の判定より先に登録する。TryAcquire は環境によっては例外を投げうるため、
            // ここより後だと「ハンドラ未登録のまま無言でクラッシュする」経路ができてしまう
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 相対パスのまま送ると、作業ディレクトリの違う既存インスタンス側では解決できない。
            // 存在確認はこのプロセスの作業ディレクトリ基準で行い、送るのは絶対パスに直したもの
            var filePaths = e.Args.Where(System.IO.File.Exists)
                                  .Select(System.IO.Path.GetFullPath)
                                  .ToArray();

            // 既に起動中のインスタンスがある場合は、ファイルパスをそちらへ渡して自分はウィンドウを作らず終了する
            if (!TryAcquireSingleInstance())
            {
                if (filePaths.Length > 0)
                    SendToRunningInstanceOrNotify(filePaths);
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            _ipcListenTask = SingleInstanceService.StartListening(OnFilesReceivedFromOtherInstance, _ipcListenCts.Token);
        }

        /// <summary>単一起動用のミューテックス取得を試みる。取得処理自体が失敗した場合は単独起動として扱う。</summary>
        private static bool TryAcquireSingleInstance()
        {
            try
            {
                return SingleInstanceService.TryAcquire();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
            {
                // 権限の異なるセッションが同名ミューテックスを保持している等で取得自体に失敗することがある。
                // 二重起動の抑止はできないが、アプリが起動しないよりは単独起動として続行するほうがよい
                DiagnosticLog.WriteFatal("ipc", $"単一起動の判定に失敗したため単独起動として続行する: {ex}");
                return true;
            }
        }

        private static void SendToRunningInstanceOrNotify(string[] filePaths)
        {
            if (SingleInstanceService.TrySendToRunningInstance(filePaths, (int)SendToRunningInstanceTimeout.TotalMilliseconds))
                return;

            // 送れなかったことを黙って捨てると、ユーザーからは「ダブルクリックしても何も起きない」ように見える
            MessageBox.Show(
                "起動中の MultiTrackPlayer にファイルを渡せませんでした。\n" +
                "起動中のウィンドウへ直接ドラッグ＆ドロップしてください。",
                "MultiTrackPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 二重起動側のプロセスからファイルパスを受け取ったとき、プレイリスト末尾に追加して再生するのは
        // UI スレッドで行う必要があるため（受信自体はバックグラウンドのパイプ待受スレッド）
        private void OnFilesReceivedFromOtherInstance(string[] files)
            => Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                    mainWindow.HandleFilesFromAnotherInstance(files);
            });

        protected override void OnExit(ExitEventArgs e)
        {
            _ipcListenCts.Cancel();
            // 受信ループがパイプを閉じ切る前にミューテックスを解放すると、直後に起動した
            // 新しいインスタンスがパイプを生成できず取りこぼす。停止を見届けてから解放する
            WaitForIpcShutdown();
            _ipcListenCts.Dispose();
            SingleInstanceService.Release();
            base.OnExit(e);
        }

        private void WaitForIpcShutdown()
        {
            if (_ipcListenTask == null) return;
            try
            {
                if (!_ipcListenTask.Wait(IpcShutdownTimeout))
                    // 見届けられないまま次行で Mutex を解放することになる。直後に起動した
                    // 新インスタンスがパイプを生成できず取りこぼす可能性があるため必ず記録する
                    DiagnosticLog.WriteFatal("ipc", "IPC 受信ループが時間内に停止しなかった（この後ミューテックスを解放する）");
            }
            catch (AggregateException ex)
            {
                DiagnosticLog.WriteFatal("ipc", $"IPC 受信ループが例外で終了した: {ex}");
            }
        }

        // 例外を握りつぶさず、診断ログに残してから既定の異常終了動作に委ねる。
        // e.Handled は意図的に設定しない（原因不明の状態で動作を続けるより、異常終了させて記録を残す）。
        // WriteFatal はデバッグモードが無効でも記録するため、初回のクラッシュでも手がかりが残る。
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
            => DiagnosticLog.WriteFatal("fatal", $"AppDomain.UnhandledException isTerminating={e.IsTerminating}: {e.ExceptionObject}");

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
            => DiagnosticLog.WriteFatal("fatal", $"Dispatcher.UnhandledException: {e.Exception}");

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
            => DiagnosticLog.WriteFatal("fatal", $"TaskScheduler.UnobservedTaskException: {e.Exception}");
    }
}
