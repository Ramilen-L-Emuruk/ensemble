namespace MultiTrackPlayer.Core.Models;

/// <summary>
/// エンジンのネイティブフレームリングから借用した1フレーム。呼び出し側は PixelBuffer を
/// 読み終えたら IMediaEngine.ReturnFrame(lease) で必ず返却すること（中間の byte[] 確保を避けるため）。
/// </summary>
public sealed record VideoFrameLease(
    int SlotIndex,
    IntPtr PixelBuffer,
    int Width,
    int Height,
    int Stride,
    TimeSpan Pts);
