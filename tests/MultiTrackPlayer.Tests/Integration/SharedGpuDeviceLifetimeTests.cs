using System.Runtime.InteropServices;
using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Rendering;
using MultiTrackPlayer.Engine.Utilities;
using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 描画とデコードで共有する D3D11 デバイスが、FFmpeg の D3D11VA デバイスコンテキストと
/// 参照数で釣り合うことを確かめる。
/// </summary>
/// <remarks>
/// <b>釣り合いが崩れると、症状は解放の瞬間ではなく後から出る。</b> 解放済み COM への
/// <c>Release</c> は未定義動作で、壊れたヒープが後になって別の顔で表面化する。
/// そのため<b>クラッシュを待たずに参照数を直接数える</b>——機構と実測値は
/// <c>HardwareAccel.CreateD3D11VAContextFromDevice</c> の doc が単一の情報源。
/// <para>
/// <b>GPU が無い環境では意味のある検証ができない。その場合は飛ばさずに失敗させる</b>——
/// 黙って緑になると「検証したつもり」になる。<b>両方のテストが冒頭で
/// <see cref="GpuDeviceContext"/> を直接作る</b>のはそのため。
/// <see cref="MediaEngine"/> 側は GPU 生成の失敗を握り潰して SW デコードへ縮退するので、
/// エンジン越しに開くだけでは<b>修正箇所を一度も通らずに通過してしまう</b>。
/// </para>
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed unsafe class SharedGpuDeviceLifetimeTests
{
    /// <summary>
    /// 参照数を数える。<c>AddRef</c> の戻り値から 1 引いた値が現在の数。
    /// </summary>
    /// <remarks>
    /// COM の参照数は<b>デバッグ以外で当てにしてはいけない</b>値だが、ここで見ているのは
    /// 絶対値ではなく<b>ある操作の前後の差</b>なので、その用途では信頼できる。
    /// </remarks>
    private static int CountRefs(IntPtr p)
    {
        int afterAddRef = Marshal.AddRef(p);
        Marshal.Release(p);
        return afterAddRef - 1;
    }

    [Fact(DisplayName = "D3D11VA デバイスコンテキストの生成と破棄でデバイスの参照数が釣り合う")]
    public void HwDeviceContextLifecycle_LeavesDeviceRefCountUnchanged()
    {
        using var gpu = new GpuDeviceContext();
        IntPtr device = gpu.NativeDevicePointer;

        // **自分の参照を 1 つ余分に握ってから測る。** 釣り合いが崩れて 0 まで落ちた場合に、
        // 解放済みオブジェクトを触って別の壊れ方をするのを防ぐ
        Marshal.AddRef(device);
        try
        {
            int before = CountRefs(device);

            AVBufferRef* hw = HardwareAccel.CreateD3D11VAContextFromDevice(device);
            Assert.True(hw != null, "D3D11VA デバイスコンテキストを作れなかった");
            ffmpeg.av_buffer_unref(&hw);

            int after = CountRefs(device);

            // 収支が 0 でなければ、その分だけ多く（または少なく）解放されている。
            // 内訳は HardwareAccel.CreateD3D11VAContextFromDevice の doc
            Assert.Equal(before, after);
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    [Fact(DisplayName = "1 プロセスで映像付きファイルを複数のエンジンが開いても壊れない")]
    public void MultipleEngines_OpeningVideoFile_DoNotCorruptSharedDevice()
    {
        // **GPU が無ければここで失敗させる。** MediaEngine は GPU 生成の失敗を握り潰して
        // SW デコードへ落ちるため、この確認が無いと修正箇所を通らないまま緑になる
        using (var probe = new GpuDeviceContext())
        {
            Assert.NotEqual(IntPtr.Zero, probe.NativeDevicePointer);
        }

        // **これが実際に踏んだ症状の通し確認。** 落ち方はアサーション失敗ではなく
        // **プロセスの異常終了**になるため、原因の切り分けには上の参照数のテストを見ること
        //（このクラスを ProcessWideStateCollection に入れて直列化しているのは、
        //  異常終了が並列に走る無関係なテストを道連れにしないため）
        using var media = new TestMediaFixture();
        string path = media.ThreeTracks;

        for (int i = 0; i < EngineCycles; i++)
        {
            using var harness = new MediaEngineHarness();
            harness.Engine.Open(path);
            Assert.NotNull(harness.Engine.CurrentMedia);

            // **修正箇所を本当に通ったことまで確かめる。** GPU はあるが D3D11VA の初期化だけ
            // 失敗する環境（一部のドライバ・WARP・RDP 越し等）では、MediaEngine が静かに
            // 「ファイルごとに自前生成」へ落ちる。そこを通ると **HardwareAccel
            // .CreateD3D11VAContextFromDevice を一度も踏まないまま緑になる**。
            // 見ているのは**生成の成否**で、デコーダが実際に使ったかではない（プロパティの doc）
            Assert.True(harness.Engine.IsSharedHwDeviceContextCreated,
                $"{i + 1} 個目のエンジンが共有 D3D11VA コンテキストを作れていない"
                + "（この環境では検証したい経路を通れていない）");
        }

        // 破棄後に走るファイナライザが、解放済みのデバイスを触らないこと
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// 上のテストでエンジンを作り直す回数。
    /// </summary>
    /// <remarks>
    /// <b>検出率を実測して決めた値。</b> 修正（<c>HardwareAccel</c> の <c>AddRef</c>）を外した状態で
    /// このテストだけを 10 回走らせたところ、<b>10 回とも</b>テストホストが異常終了した。
    /// 2 回では釣り合いの崩れが 1 回分しか積まれず取りこぼす可能性があるため 3 回にしてある。
    /// <para>
    /// <b>偽陽性は原理的に出ない。</b> 参照数が釣り合っていれば、何回繰り返しても
    /// 解放済みオブジェクトには触らない。<b>通ったことは保証ではなく反証の不在</b>だが、
    /// 釣り合いそのものは上の参照数のテストが決定的に見ている。
    /// </para>
    /// </remarks>
    private const int EngineCycles = 3;
}
