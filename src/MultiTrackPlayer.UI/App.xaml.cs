using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

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
        }
    }
}
