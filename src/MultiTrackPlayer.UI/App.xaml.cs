using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
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

        private readonly CancellationTokenSource _ipcListenCts = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            StartupArgs = e.Args;

            var filePaths = e.Args.Where(System.IO.File.Exists).ToArray();

            // 既に起動中のインスタンスがある場合は、ファイルパスをそちらへ渡して自分はウィンドウを作らず終了する
            if (!SingleInstanceService.TryAcquire())
            {
                if (filePaths.Length > 0)
                    SingleInstanceService.TrySendToRunningInstance(filePaths);
                Shutdown();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            SingleInstanceService.StartListening(OnFilesReceivedFromOtherInstance, _ipcListenCts.Token);
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
            SingleInstanceService.Release();
            base.OnExit(e);
        }

        // 例外を握りつぶさず、診断ログに残してから既定の異常終了動作に委ねる
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
            => DiagnosticLog.Write("fatal", $"AppDomain.UnhandledException isTerminating={e.IsTerminating}: {e.ExceptionObject}");

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
            => DiagnosticLog.Write("fatal", $"Dispatcher.UnhandledException: {e.Exception}");

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
            => DiagnosticLog.Write("fatal", $"TaskScheduler.UnobservedTaskException: {e.Exception}");
    }
}
