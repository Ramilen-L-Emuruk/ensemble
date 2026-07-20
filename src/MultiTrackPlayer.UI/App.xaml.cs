using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>起動時のコマンドライン引数（動画ファイルパス）。MainWindow が Loaded 時に参照する。</summary>
        public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            StartupArgs = e.Args;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
