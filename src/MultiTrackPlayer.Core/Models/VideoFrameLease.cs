namespace MultiTrackPlayer.Core.Models;

/// <summary>フレームの受け渡し形態。<see cref="VideoFrameLease"/> のどのペイロードが有効かを示す。</summary>
public enum FrameKind
{
    /// <summary>CPU 上の BGRA バッファ（<see cref="VideoFrameLease.PixelBuffer"/> が有効）。</summary>
    Cpu,
    /// <summary>GPU 上の共有 BGRA テクスチャ（<see cref="VideoFrameLease.SharedSurfaceHandle"/> が有効）。</summary>
    Gpu,
}

/// <summary>
/// エンジンのフレームリングから借用した1フレーム。呼び出し側は読み終えたら
/// IMediaEngine.ReturnFrame(lease) で必ず返却すること（中間の byte[] 確保を避けるため）。
///
/// <see cref="Kind"/> により有効なペイロードが変わる:
/// <list type="bullet">
/// <item><see cref="FrameKind.Cpu"/>: <see cref="PixelBuffer"/>/<see cref="Stride"/> が有効（<see cref="SharedSurfaceHandle"/> は <see cref="IntPtr.Zero"/>）。</item>
/// <item><see cref="FrameKind.Gpu"/>: <see cref="SharedSurfaceHandle"/> が有効（<see cref="PixelBuffer"/> は <see cref="IntPtr.Zero"/>、<see cref="Stride"/> は 0）。</item>
/// </list>
/// </summary>
/// <param name="SlotIndex">リング内のスロット番号（返却時の識別に使う）。</param>
/// <param name="Kind">有効なペイロードの種別。</param>
/// <param name="PixelBuffer">CPU 経路の BGRA ネイティブバッファ先頭（GPU 経路では <see cref="IntPtr.Zero"/>）。</param>
/// <param name="Width">フレーム幅（px）。</param>
/// <param name="Height">フレーム高さ（px）。</param>
/// <param name="Stride">CPU 経路の1行あたりバイト数（GPU 経路では 0）。</param>
/// <param name="SharedSurfaceHandle">GPU 経路の共有 BGRA テクスチャの共有ハンドル（CPU 経路では <see cref="IntPtr.Zero"/>）。</param>
/// <param name="Pts">このフレームの表示タイムスタンプ。</param>
public sealed record VideoFrameLease(
    int SlotIndex,
    FrameKind Kind,
    IntPtr PixelBuffer,
    int Width,
    int Height,
    int Stride,
    IntPtr SharedSurfaceHandle,
    TimeSpan Pts);
