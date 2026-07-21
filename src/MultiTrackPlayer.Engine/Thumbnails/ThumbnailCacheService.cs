using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Diagnostics;

namespace MultiTrackPlayer.Engine.Thumbnails;

/// <summary>
/// ファイルを開いた際にシークバー用サムネイルのキャッシュ確認・バックグラウンド生成を行う。
/// 生成はUIスレッドをブロックしない別スレッドで実行し、ファイル切替時は前回の生成をキャンセルする。
/// </summary>
public sealed class ThumbnailCacheService
{
    private const int DefaultTileWidth = 160;

    private CancellationTokenSource? _cts;

    /// <summary>キャッシュ済みが見つかった、または生成が完了した時に発火する。失敗時は null を渡す。</summary>
    public event EventHandler<ThumbnailSheet?>? ThumbnailsReady;

    public void RequestForFile(string filePath, TimeSpan duration, int mediaWidth, int mediaHeight)
    {
        DiagnosticLog.Write("thumbnail",
            $"RequestForFile path={filePath} duration={duration.TotalSeconds:F1} w={mediaWidth} h={mediaHeight}");

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var cached = ThumbnailCacheStore.Load(filePath);
        if (cached != null)
        {
            DiagnosticLog.Write("thumbnail", $"キャッシュ命中 path={filePath} sheet={cached.SheetPath}");
            ThumbnailsReady?.Invoke(this, cached);
            return;
        }

        if (duration <= TimeSpan.Zero || mediaWidth <= 0 || mediaHeight <= 0)
        {
            DiagnosticLog.Write("thumbnail", $"生成スキップ（パラメータ不正） path={filePath}");
            ThumbnailsReady?.Invoke(this, null);
            return;
        }

        DiagnosticLog.Write("thumbnail", $"バックグラウンド生成開始 path={filePath}");
        Task.Run(() => GenerateAndPublish(filePath, duration.TotalSeconds, mediaWidth, mediaHeight, cts));
    }

    private void GenerateAndPublish(
        string filePath, double durationSeconds, int mediaWidth, int mediaHeight, CancellationTokenSource cts)
    {
        try
        {
            var (jpgPath, _) = ThumbnailCacheStore.GetCachePaths(filePath);
            ThumbnailCacheStore.EnsureStorageDir();

            var sheet = ThumbnailGenerator.Generate(
                filePath, jpgPath, durationSeconds, mediaWidth, mediaHeight,
                DefaultTileWidth, cts.Token,
                onProgress: partial =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    DiagnosticLog.Write("thumbnail", $"進捗更新 path={filePath} version={partial.Version}");
                    ThumbnailsReady?.Invoke(this, partial);
                });

            if (cts.Token.IsCancellationRequested)
            {
                DiagnosticLog.Write("thumbnail", $"生成キャンセル path={filePath}");
                return;
            }

            if (sheet == null)
            {
                DiagnosticLog.Write("thumbnail", $"生成失敗（Generate が null を返却） path={filePath}");
                ThumbnailsReady?.Invoke(this, null);
                return;
            }

            ThumbnailCacheStore.SaveIndex(sheet);
            DiagnosticLog.Write("thumbnail", $"生成完了 path={filePath} count={sheet.Count} sheet={sheet.SheetPath}");
            if (!cts.Token.IsCancellationRequested)
                ThumbnailsReady?.Invoke(this, sheet);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("thumbnail", $"生成失敗（例外） path={filePath} ex={ex}");
            if (!cts.Token.IsCancellationRequested)
                ThumbnailsReady?.Invoke(this, null);
        }
    }
}
