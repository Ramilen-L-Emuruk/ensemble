using MultiTrackPlayer.Engine.Diagnostics;
using Sdcb.FFmpeg.Raw;
using System.Runtime.InteropServices;
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
    /// <b>FFmpeg は注入された device の参照を 1 つ譲り受ける</b>（<c>AVD3D11VADeviceContext.device</c> の
    /// 仕様: 「Deallocating the AVHWDeviceContext will always release this interface, and it does not
    /// matter whether it was user-allocated」）。<b>そのため渡す前に <c>AddRef</c> する。</b>
    /// 初期化に失敗（av_hwdevice_ctx_init &lt; 0）した場合は alloc 済みのバッファを解放し null を返す。
    /// </summary>
    /// <remarks>
    /// <b>AddRef を外さないこと。外すと呼び出し元のデバイスが二重解放になる。</b>
    /// 以前はここに「FFmpeg 側が自身で AddRef するので呼び出し側は自分の参照を保持し続けてよい」と
    /// 書いてあったが、<b>事実と逆だった</b>。同梱の FFmpeg 6.0 で参照数を実測した結果:
    /// <list type="bullet">
    /// <item><c>av_hwdevice_ctx_init</c> で <b>+1</b>（<c>video_device</c> の <c>QueryInterface</c>。
    /// <c>ID3D11VideoDevice</c> はデバイスと同一オブジェクトなので参照数を共有する）</item>
    /// <item><c>av_buffer_unref</c> で <b>−2</b>（注入した <c>device</c> と、上の <c>video_device</c>）</item>
    /// <item>往復の収支は <b>−1</b>。AddRef を足すと <b>0</b> になる</item>
    /// </list>
    /// <para>
    /// <b>症状は解放の瞬間には出ない。</b> 解放済み COM への <c>Release</c> は未定義動作で、
    /// 壊れたヒープは後から表面化する——実際に踏んだのは「1 プロセスで 2 つ目の
    /// <c>MediaEngine</c> が映像付きファイルを開くとプロセスが落ちる」という形だった
    /// （本番はエンジンを 1 つしか作らないため見えていなかっただけ）。
    /// </para>
    /// </remarks>
    /// <param name="d3d11DevicePtr">注入する <c>ID3D11Device</c> の生ポインタ。</param>
    /// <returns>初期化に成功した D3D11VA デバイスコンテキスト。失敗時は null。</returns>
    public unsafe static AVBufferRef* CreateD3D11VAContextFromDevice(IntPtr d3d11DevicePtr)
    {
        if (d3d11DevicePtr == IntPtr.Zero)
            return null;

        // ここから下を try/finally で包んでいないのは、確保と初期化の間に CLR 例外を投げる
        // 処理が無いため。DiagnosticLog.Write は内部で全例外を握り潰し、残りは有効なポインタへの
        // 呼び出しに限られる（無効なポインタで AccessViolationException が起きる場合は、
        // 既定では try/finally でも捕まえられないので包んでも意味が無い）
        AVBufferRef* devRef = av_hwdevice_ctx_alloc(AVHWDeviceType.D3d11va);
        if (devRef == null)
        {
            DiagnosticLog.Write("gpuDevice", "av_hwdevice_ctx_alloc 失敗（自前デバイス注入を断念）");
            return null;
        }

        AVHWDeviceContext* devCtx = (AVHWDeviceContext*)devRef->data;
        AVD3D11VADeviceContext* d3dCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;

        // **FFmpeg へ渡す分の参照をここで足す。**
        //
        // 成功経路の収支は実測済み（上の remarks）。**失敗経路（av_hwdevice_ctx_init < 0）は
        // 実測していない**——意図的に初期化を失敗させる手立てが無いため。根拠は FFmpeg の
        // d3d11va_device_uninit が **Release したポインタに NULL を代入している**ことで、
        // uninit が何度走っても Release は 1 回に留まる（初期化が内部で uninit を呼んでいても、
        // 続く av_buffer_unref の解放コールバックは何もしない）。したがって足した 1 つは
        // 成功・失敗のどちらでも 1 回だけ消費される
        Marshal.AddRef(d3d11DevicePtr);
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
