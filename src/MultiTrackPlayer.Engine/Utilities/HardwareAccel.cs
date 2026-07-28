using MultiTrackPlayer.Engine.Diagnostics;
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

    /// <summary>
    /// 既存の <c>ID3D11Device</c>（自前生成デバイス）を FFmpeg の D3D11VA デバイスコンテキストに注入して
    /// <see cref="AVBufferRef"/> を作る。描画とデコードで同一デバイスを共有するための経路。
    ///
    /// FFmpeg 側は格納された device に対して自身で AddRef するため、呼び出し側は自分の参照を保持し続けてよい。
    /// 初期化に失敗（av_hwdevice_ctx_init &lt; 0）した場合は alloc 済みのバッファを解放し null を返す。
    /// </summary>
    /// <param name="d3d11DevicePtr">注入する <c>ID3D11Device</c> の生ポインタ。</param>
    /// <returns>初期化に成功した D3D11VA デバイスコンテキスト。失敗時は null。</returns>
    public unsafe static AVBufferRef* CreateD3D11VAContextFromDevice(IntPtr d3d11DevicePtr)
    {
        if (d3d11DevicePtr == IntPtr.Zero)
            return null;

        AVBufferRef* devRef = av_hwdevice_ctx_alloc(AVHWDeviceType.D3d11va);
        if (devRef == null)
        {
            DiagnosticLog.Write("gpuDevice", "av_hwdevice_ctx_alloc 失敗（自前デバイス注入を断念）");
            return null;
        }

        AVHWDeviceContext* devCtx = (AVHWDeviceContext*)devRef->data;
        AVD3D11VADeviceContext* d3dCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;
        d3dCtx->device = (ID3D11Device*)d3d11DevicePtr;

        int ret = av_hwdevice_ctx_init(devRef);
        if (ret < 0)
        {
            DiagnosticLog.Write("gpuDevice",
                $"av_hwdevice_ctx_init 失敗 ret={ret}（自前デバイス注入を断念）");
            av_buffer_unref(&devRef);
            return null;
        }

        return devRef;
    }
}
