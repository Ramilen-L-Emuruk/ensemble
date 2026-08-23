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

        // 走行中の生成タスクへ渡した CTS はまだ Token を参照しているため、ここで Dispose すると
        // 破棄後の Token アクセスで ObjectDisposedException になる。Cancel のみ行い、Dispose はしない。
        // （Generate は Token.IsCancellationRequested しか使わず WaitHandle を生成しないためリークは実害なし）
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var cached = ThumbnailCacheStore.Load(filePath, out string? cacheError);
        // 読めなければ作り直すので致命ではないが、記録が無いと権限異常・破損が続いても気づけない
        if (cacheError != null)
            DiagnosticLog.Write("thumbnail", $"キャッシュ読み込み失敗（作り直す） path={filePath}: {cacheError}");
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
        // CancellationToken（構造体）を渡す。CTS のプロパティ（get_Token）を別スレッドから触らないことで、
        // 次のファイル切替で CTS が Cancel されても破棄後アクセスの例外が起きないようにする。
        var token = cts.Token;
        Task.Run(() => GenerateAndPublish(filePath, duration.TotalSeconds, mediaWidth, mediaHeight, token));
    }

    private void GenerateAndPublish(
        string filePath, double durationSeconds, int mediaWidth, int mediaHeight, CancellationToken token)
    {
        try
        {
            var (jpgPath, _) = ThumbnailCacheStore.GetCachePaths(filePath);
            ThumbnailCacheStore.EnsureStorageDir();

            var sheet = ThumbnailGenerator.Generate(
                filePath, jpgPath, durationSeconds, mediaWidth, mediaHeight,
                DefaultTileWidth, token,
                onProgress: partial =>
                {
                    if (token.IsCancellationRequested) return;
                    DiagnosticLog.Write("thumbnail", $"進捗更新 path={filePath} version={partial.Version}");
                    ThumbnailsReady?.Invoke(this, partial);
                });

            if (token.IsCancellationRequested)
            {
                DiagnosticLog.Write("thumbnail", $"生成キャンセル path={filePath}");
                return;
            }

            if (sheet == null)
            {
                // 失敗の記録は必ず残る側へ。診断ログは既定で無効なので、Write では
                // 「シークバーにサムネイルが出ない」の原因を後から追えない
                DiagnosticLog.WriteFatal("thumbnail", $"生成失敗（Generate が null を返却） path={filePath}");
                ThumbnailsReady?.Invoke(this, null);
                return;
            }

            ThumbnailCacheStore.SaveIndex(sheet);
            DiagnosticLog.Write("thumbnail", $"生成完了 path={filePath} count={sheet.Count} sheet={sheet.SheetPath}");
            if (!token.IsCancellationRequested)
                ThumbnailsReady?.Invoke(this, sheet);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFatal("thumbnail", $"生成失敗（例外） path={filePath} ex={ex}");
            if (!token.IsCancellationRequested)
                ThumbnailsReady?.Invoke(this, null);
        }
    }
}
