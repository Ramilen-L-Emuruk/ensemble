using MultiTrackPlayer.Engine.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>リースされた GPU フレームの参照（UI へ渡す用）。SlotIndex と SharedHandle で共有テクスチャを特定する。</summary>
public readonly record struct GpuRingFrame(int SlotIndex, IntPtr SharedHandle, int Width, int Height, double PtsSeconds);

/// <summary>
/// GPU ゼロコピー描画用の固定長リング。各スロットは共有可能な BGRA テクスチャ（<see cref="Format.B8G8R8A8_UNorm"/>,
/// <see cref="ResourceUsage.Default"/>, <see cref="BindFlags.RenderTarget"/>, <see cref="ResourceOptionFlags.Shared"/>）を持ち、
/// 書き込み側（<c>GpuFrameConverter</c>）が <see cref="ID3D11VideoProcessorOutputView"/> 経由で VideoProcessorBlt の出力先に使う。
/// 読み取り側（UI）へは <see cref="GpuRingFrame.SharedHandle"/> を渡し、別デバイスから共有ハンドルで開いて描画する。
///
/// 状態機械（Free/Writing/Ready/Leased・serial 世代・Flush/EOF・Close）は <see cref="SlotSequencer"/> に委譲し、
/// 本クラスはスロットごとのテクスチャ／共有ハンドル／OutputView の確保・解放だけを担う（<see cref="VideoFrameRing"/> と同じ委譲パターン）。
///
/// OutputView 生成には <see cref="ID3D11VideoProcessorEnumerator"/> が要るが、それは書き込み側の <c>GpuFrameConverter</c> が
/// 所有する。循環依存を避けるため、本クラスは Converter を参照せず、Converter から <see cref="SetEnumerator"/> で enumerator を
/// 借用し、<see cref="GetOutputView"/> 内で遅延生成する（依存は Converter → Ring の一方向のみ）。
/// </summary>
public sealed class GpuVideoFrameRing : IDisposable
{
    private const int SlotCount = 4;

    /// <summary>BeginWrite の戻り値: Close 済み。</summary>
    public const int SlotClosed = SlotSequencer.SlotClosed;
    /// <summary>BeginWrite の戻り値: 空き待ち中に Flush が発生（このフレームは破棄すべき）。</summary>
    public const int SlotFlushed = SlotSequencer.SlotFlushed;

    private sealed class Payload
    {
        public ID3D11Texture2D? Texture;
        public IntPtr SharedHandle;
        public ID3D11VideoProcessorOutputView? OutputView;
        public int Width, Height;
    }

    private readonly SlotSequencer _seq = new(SlotCount);
    private readonly Payload[] _payloads;
    private readonly GpuDeviceContext _gpu;

    // Converter から借用する enumerator（所有しない）。OutputView 生成に使う。
    private ID3D11VideoProcessorEnumerator? _enumerator;

    public GpuVideoFrameRing(GpuDeviceContext gpu)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _payloads = new Payload[SlotCount];
        for (int i = 0; i < SlotCount; i++) _payloads[i] = new Payload();
    }

    public int CurrentSerial => _seq.CurrentSerial;

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
    /// Free スロットが空くまでブロックする。Close 済みなら <see cref="SlotClosed"/>、待機中に Flush が起きたら <see cref="SlotFlushed"/>。
    /// 確保できたスロットは、サイズが変わっていれば共有テクスチャ＋共有ハンドルを作り直す（OutputView は enumerator に依存するため遅延生成）。
    /// </summary>
    public int BeginWrite(int width, int height)
    {
        return _seq.BeginWrite(idx =>
        {
            var p = _payloads[idx];
            if (p.Texture == null || p.Width != width || p.Height != height)
            {
                DisposePayloadResources(p);
                CreateSlotTexture(p, width, height);
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

    public void CommitWrite(int slotIndex, double ptsSeconds) => _seq.CommitWrite(slotIndex, ptsSeconds);

    public void AbortWrite(int slotIndex) => _seq.AbortWrite(slotIndex);

    /// <summary>クロック位置に対して due な最新フレームを1枚リースする。何も due でなければ false。読み終えたら必ず <see cref="ReturnLease"/> すること。</summary>
    public bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds, out GpuRingFrame frame, out int droppedCount)
    {
        if (_seq.TryLeaseDue(clockPositionSeconds, frameDurationSeconds, out int idx, out double pts, out droppedCount))
        {
            var p = _payloads[idx];
            frame = new GpuRingFrame(idx, p.SharedHandle, p.Width, p.Height, pts);
            return true;
        }
        frame = default;
        return false;
    }

    /// <summary>最も古い Ready フレームを1枚リースする（クロック非依存。Step・一時停止中シーク用）。timeout 内に無ければ false。</summary>
    public bool TryLeaseOldest(TimeSpan timeout, int minSerial, out GpuRingFrame frame)
    {
        if (_seq.TryLeaseOldest(timeout, minSerial, out int idx, out double pts))
        {
            var p = _payloads[idx];
            frame = new GpuRingFrame(idx, p.SharedHandle, p.Width, p.Height, pts);
            return true;
        }
        frame = default;
        return false;
    }

    /// <summary>リース中のスロットを Free に戻す。Close 済みで PendingFreeOnReturn なら、ここでテクスチャ／OutputView も解放する。</summary>
    public void ReturnLease(int slotIndex) => _seq.ReturnLease(slotIndex, FreeSlot);

    /// <summary>シーク時: 世代番号を進めて Ready を Free に戻し EOF 状態も解除する（どのスレッドから呼んでも安全）。</summary>
    public void Flush() => _seq.Flush();

    /// <summary>診断用: 全スロットの状態スナップショット。</summary>
    public string DescribeSlots() => _seq.DescribeSlots();

    /// <summary>producer が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof() => _seq.MarkEof();

    /// <summary>EOF 済みかつ表示待ち・リース中のフレームが残っていない（再生完了検出に使う）。</summary>
    public bool IsEofDrained => _seq.IsEofDrained;

    public void Close() => _seq.Close();

    /// <summary>Close 後に解放する。Leased 中のスロットは UI がまだ参照している可能性があるため <see cref="ReturnLease"/> 時に解放する。</summary>
    public void Dispose()
    {
        Close();
        // enumerator は Converter 所有のため破棄しない。テクスチャ／OutputView のみ解放する。
        _seq.DisposeSlots(FreeSlot);
    }

    private void CreateSlotTexture(Payload p, int width, int height)
    {
        // 別デバイス（描画側）から共有ハンドルで開けるよう Shared フラグ付きの BGRA レンダーターゲットを作る。
        p.Texture = _gpu.Device.CreateTexture2D(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, arraySize: 1, mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.RenderTarget,
            miscFlags: ResourceOptionFlags.Shared,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None);

        using var dxgiResource = p.Texture.QueryInterface<IDXGIResource>();
        p.SharedHandle = dxgiResource.SharedHandle;
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
