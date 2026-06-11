using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Utilities;

public static class HardwareAccel
{
    public unsafe static AVBufferRef* TryCreateD3D11VAContext()
    {
        AVBufferRef* hwCtx = null;
        int ret = av_hwdevice_ctx_create(&hwCtx, AVHWDeviceType.D3d11va, null, null, 0);
        if (ret < 0)
            return null;
        return hwCtx;
    }
}
