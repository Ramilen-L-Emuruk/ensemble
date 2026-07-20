using System.Runtime.InteropServices;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Utilities;

/// <summary>FFmpeg の AVERROR コード（負値）を av_strerror 経由で人間可読な文字列に変換する。</summary>
public static unsafe class FFmpegError
{
    private const int BufferSize = 128;

    public static string Describe(int averror)
    {
        byte* buf = stackalloc byte[BufferSize];
        av_strerror(averror, buf, (ulong)BufferSize);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"unknown error ({averror})";
    }
}
