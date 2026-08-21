using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// GPU ゼロコピー描画用の固定長リング。各スロットは共有可能な BGRA テクスチャ（<see cref="Format.B8G8R8A8_UNorm"/>,
/// <see cref="ResourceUsage.Default"/>, <see cref="BindFlags.RenderTarget"/>, <see cref="ResourceOptionFlags.Shared"/>）を持ち、
/// 書き込み側（<c>GpuFrameConverter</c>）が <see cref="ID3D11VideoProcessorOutputView"/> 経由で VideoProcessorBlt の出力先に使う。
/// 読み取り側（UI）へは <see cref="GpuRingFrame.SharedHandle"/> を渡し、別デバイスから共有ハンドルで開いて描画する。
///
/// 状態機械（Free/Writing/Ready/Leased・シーク世代・Flush/EOF・Close）は <see cref="SlotSequencer"/> に委譲し、
/// 本クラスはスロットごとのテクスチャ／共有ハンドル／OutputView の確保・解放だけを担う（<see cref="VideoFrameRing"/> と同じ委譲パターン）。
///
/// OutputView 生成には <see cref="ID3D11VideoProcessorEnumerator"/> が要るが、それは書き込み側の <c>GpuFrameConverter</c> が
/// 所有する。循環依存を避けるため、本クラスは Converter を参照せず、Converter から <see cref="SetEnumerator"/> で enumerator を
/// 借用し、<see cref="GetOutputView"/> 内で遅延生成する（依存は Converter → Ring の一方向のみ）。
/// </summary>
public sealed class GpuVideoFrameRing : IVideoFrameRing
{
    private const int SlotCount = 4;

    /// <summary>BeginWrite の戻り値: Close 済み。</summary>
    public const int SlotClosed = SlotSequencer.SlotClosed;
    /// <summary>BeginWrite の戻り値: 書き込もうとしたフレームがすでに過去の世代（このフレームは破棄すべき）。</summary>
    public const int SlotFlushed = SlotSequencer.SlotFlushed;

    private sealed class Payload
    {
        public ID3D11Texture2D? Texture;
        public IntPtr SharedHandle;
        public ID3D11VideoProcessorOutputView? OutputView;
        public int Width, Height;
    }

    private readonly SlotSequencer _seq;
    private readonly Payload[] _payloads;
    private readonly GpuDeviceContext _gpu;

    // Converter から借用する enumerator（所有しない）。OutputView 生成に使う。
    private ID3D11VideoProcessorEnumerator? _enumerator;

    public GpuVideoFrameRing(GpuDeviceContext gpu)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _payloads = new Payload[SlotCount];
        for (int i = 0; i < SlotCount; i++) _payloads[i] = new Payload();
        // _payloads を先に用意してから渡す（コールバックは後から呼ばれるが、順序を崩すと null 参照になる）
        _seq = new SlotSequencer(SlotCount, FreeSlot);
    }

    public SeekEpoch CurrentEpoch => _seq.CurrentEpoch;

    /// <summary>
    /// 書き込み側（Converter）が OutputView を生成するための enumerator を渡す。enumerator が差し替わった場合は、
    /// 既存の OutputView は旧 enumerator に紐づくため破棄し、次回 <see cref="GetOutputView"/> で作り直させる。
    /// </summary>
    public void SetEnumerator(ID3D11VideoProcessorEnumerator enumerator)
    {
        if (ReferenceEquals(_enumerator, enumerator)) return;
        _enumerator = enumerator;
        foreach (var p in _payloads)
        {
            p.OutputView?.Dispose();
            p.OutputView = null;
        }
    }

    /// <summary>
    /// Free スロットが空くまでブロックする。Close 済みなら <see cref="SlotClosed"/>、
    /// <paramref name="epoch"/> が現在の世代と一致しなければ <see cref="SlotFlushed"/>。
    /// 確保できたスロットは、サイズが変わっていれば共有テクスチャ＋共有ハンドルを作り直す（OutputView は enumerator に依存するため遅延生成）。
    /// </summary>
    /// <param name="epoch">書き込むフレームを産んだパケットの世代（<see cref="SlotSequencer.BeginWrite"/> 参照）。</param>
    public int BeginWrite(int width, int height, SeekEpoch epoch)
    {
        return _seq.BeginWrite(epoch, idx =>
        {
            var p = _payloads[idx];
            if (p.Texture == null || p.Width != width || p.Height != height)
            {
                // 先に新しいテクスチャを作り、成功してから旧リソースを解放する
                //（CPU 版 VideoFrameRing.BeginWrite と同じ alloc-then-free 原則）。
                // 逆順にすると生成が失敗したときにスロットがテクスチャ無しの壊れた状態で残る
                var (texture, sharedHandle) = CreateSlotTexture(width, height);
                DisposePayloadResources(p);
                p.Texture = texture;
                p.SharedHandle = sharedHandle;
                p.Width = width;
                p.Height = height;
            }
        });
    }

    /// <summary>
    /// 書き込み側（Converter）が VideoProcessorBlt の出力先に使う OutputView を取得する。未生成なら enumerator から遅延生成する。
    /// <see cref="SetEnumerator"/> が未呼び出しの場合は例外。
    /// </summary>
    public ID3D11VideoProcessorOutputView GetOutputView(int slotIndex)
    {
        var p = _payloads[slotIndex];
        if (p.Texture == null)
            throw new InvalidOperationException($"slot {slotIndex} のテクスチャが未確保です（BeginWrite 前に GetOutputView が呼ばれた）");
        if (p.OutputView != null) return p.OutputView;

        if (_enumerator == null)
            throw new InvalidOperationException("enumerator が未設定です（SetEnumerator を先に呼ぶ必要があります）");
        var videoDevice = _gpu.VideoDevice
            ?? throw new InvalidOperationException("VideoDevice が無い環境のため OutputView を生成できません");

        var desc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        p.OutputView = videoDevice.CreateVideoProcessorOutputView(p.Texture, _enumerator, desc);
        return p.OutputView;
    }

    /// <summary>スロットの共有 BGRA テクスチャを返す（vout の swapchain 提示でバックバッファへコピーする用。未確保なら null）。</summary>
    internal ID3D11Texture2D? GetSlotTexture(int slotIndex) => _payloads[slotIndex].Texture;

    public void CommitWrite(int slotIndex, double ptsSeconds) => _seq.CommitWrite(slotIndex, ptsSeconds);

    public void AbortWrite(int slotIndex) => _seq.AbortWrite(slotIndex);

    /// <summary>クロック位置に対して due な最新フレームを1枚リースする。何も due でなければ false。読み終えたら必ず <see cref="ReturnLease"/> すること。</summary>
    public bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds, out VideoFrameLease? lease, out int droppedCount)
    {
        if (_seq.TryLeaseDue(clockPositionSeconds, frameDurationSeconds, out int idx, out double pts, out droppedCount))
        {
            var p = _payloads[idx];
            lease = new VideoFrameLease(idx, FrameKind.Gpu, PixelBuffer: IntPtr.Zero, p.Width, p.Height,
                Stride: 0, p.SharedHandle, TimeSpan.FromSeconds(pts));
            return true;
        }
        lease = null;
        return false;
    }

    /// <summary>
    /// 最も古い Ready フレームを1枚リースする（クロック非依存。Step・一時停止中シーク用）。
    /// <paramref name="epoch"/> と世代が一致するスロットだけを対象にする。timeout 内に無ければ false。
    /// </summary>
    public bool TryLeaseOldest(TimeSpan timeout, SeekEpoch epoch, out VideoFrameLease? lease)
    {
        if (_seq.TryLeaseOldest(timeout, epoch, out int idx, out double pts))
        {
            var p = _payloads[idx];
            lease = new VideoFrameLease(idx, FrameKind.Gpu, PixelBuffer: IntPtr.Zero, p.Width, p.Height,
                Stride: 0, p.SharedHandle, TimeSpan.FromSeconds(pts));
            return true;
        }
        lease = null;
        return false;
    }

    /// <summary>リース中のスロットを Free に戻す。破棄時に遅延解放が予約されていれば、ここでテクスチャ／OutputView も解放する。</summary>
    public void ReturnLease(int slotIndex) => _seq.ReturnLease(slotIndex);

    /// <summary>
    /// シーク時: 世代を <paramref name="epoch"/> へ更新して Ready を Free に戻し EOF 状態も解除する
    /// （どのスレッドから呼んでも安全）。戻り値の意味は <see cref="SlotSequencer.Flush"/> を参照。
    /// </summary>
    public bool Flush(SeekEpoch epoch) => _seq.Flush(epoch);

    /// <summary>診断用: 全スロットの状態スナップショット。</summary>
    public string DescribeSlots() => _seq.DescribeSlots();

    /// <summary>producer が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof() => _seq.MarkEof();

    /// <summary>EOF 済みかつ表示待ち（書き込み中・提示待ち）のフレームが残っていない。リース中は数えない（再生完了検出に使う）。</summary>
    public bool IsEofDrained => _seq.IsEofDrained;

    public void Close() => _seq.Close();

    /// <summary>
    /// Close 後に解放する。UI・vout スレッドがリース中のスロットと、デコーダが書き込み中のスロットは
    /// 参照が離れる時に解放する（<see cref="SlotSequencer.DisposeSlots"/>）。
    /// </summary>
    public void Dispose()
    {
        Close();
        // enumerator は Converter 所有のため破棄しない。テクスチャ／OutputView のみ解放する。
        _seq.DisposeSlots();
    }

    /// <summary>
    /// スロット用の共有 BGRA テクスチャと共有ハンドルを新規に作る。呼び出し側は成功した戻り値を
    /// スロットへ入れてから旧リソースを解放すること（alloc-then-free）。
    /// </summary>
    private (ID3D11Texture2D Texture, IntPtr SharedHandle) CreateSlotTexture(int width, int height)
    {
        // 別デバイス（描画側）から共有ハンドルで開けるよう Shared フラグ付きの BGRA レンダーターゲットを作る。
        var texture = _gpu.Device.CreateTexture2D(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, arraySize: 1, mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.RenderTarget,
            miscFlags: ResourceOptionFlags.Shared,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None);

        try
        {
            using var dxgiResource = texture.QueryInterface<IDXGIResource>();
            return (texture, dxgiResource.SharedHandle);
        }
        catch
        {
            // 共有ハンドルが取れなければテクスチャは使い物にならない。呼び出し側へ渡らないぶん
            // ここで解放しないと漏れる
            texture.Dispose();
            throw;
        }
    }

    private void FreeSlot(int slotIndex) => DisposePayloadResources(_payloads[slotIndex]);

    private static void DisposePayloadResources(Payload p)
    {
        p.OutputView?.Dispose();
        p.OutputView = null;
        p.Texture?.Dispose();
        p.Texture = null;
        // 共有ハンドルはテクスチャ生成時に得た擬似ハンドルで、テクスチャ Dispose により無効化されるため 0 に戻すだけでよい。
        p.SharedHandle = IntPtr.Zero;
        p.Width = 0;
        p.Height = 0;
    }
}
