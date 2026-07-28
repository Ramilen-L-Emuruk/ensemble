using System.Runtime.InteropServices;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>リースされたフレームの生データ（エンジン内部用）。</summary>
public readonly struct RingFrame
{
    public int SlotIndex { get; }
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public double PtsSeconds { get; }

    public RingFrame(int slotIndex, IntPtr buffer, int width, int height, int stride, double ptsSeconds)
    {
        SlotIndex = slotIndex;
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PtsSeconds = ptsSeconds;
    }
}

/// <summary>
/// GPU→CPU 転送後の BGRA フレームを保持するネイティブメモリの固定長リング。
/// VideoDecodeThread（producer）が Free スロットへ書き込み、呼び出し側（consumer）が
/// due なフレームをリースして直接読み取り、読み終えたら ReturnLease で返す（プル型・ゼロコピー）。
/// スロットの状態機械は <see cref="SlotSequencer"/> に委譲し、本クラスはネイティブバッファの確保・解放だけを担う。
/// </summary>
public sealed class VideoFrameRing : IDisposable
{
    private const int SlotCount = 4;

    /// <summary>BeginWrite の戻り値: Close 済み。</summary>
    public const int SlotClosed = SlotSequencer.SlotClosed;
    /// <summary>BeginWrite の戻り値: 空き待ち中に Flush が発生（このフレームは破棄すべき）。</summary>
    public const int SlotFlushed = SlotSequencer.SlotFlushed;

    private sealed class Payload
    {
        public IntPtr Buffer = IntPtr.Zero;
        public int Capacity;
        public int Width, Height, Stride;
    }

    private readonly SlotSequencer _seq = new(SlotCount);
    private readonly Payload[] _payloads;

    public VideoFrameRing()
    {
        _payloads = new Payload[SlotCount];
        for (int i = 0; i < SlotCount; i++) _payloads[i] = new Payload();
    }

    public int CurrentSerial => _seq.CurrentSerial;

    /// <summary>
    /// Free スロットが空くまでブロックする。Close 済みなら SlotClosed、待機中に Flush が起きたら SlotFlushed。
    /// 確保できたスロットには幅×高さ×4 バイトのネイティブバッファを（必要なら作り直して）割り当てる。
    /// </summary>
    public int BeginWrite(int width, int height)
    {
        int stride = width * 4;
        int needed = stride * height;
        return _seq.BeginWrite(idx =>
        {
            var p = _payloads[idx];
            if (p.Capacity < needed)
            {
                if (p.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p.Buffer);
                p.Buffer = Marshal.AllocHGlobal(needed);
                p.Capacity = needed;
            }
            p.Width = width;
            p.Height = height;
            p.Stride = stride;
        });
    }

    public IntPtr GetWriteBuffer(int slotIndex) => _payloads[slotIndex].Buffer;

    public void CommitWrite(int slotIndex, double ptsSeconds) => _seq.CommitWrite(slotIndex, ptsSeconds);

    public void AbortWrite(int slotIndex) => _seq.AbortWrite(slotIndex);

    /// <summary>
    /// クロック位置に対して due な最新フレームを1枚リースする。選ばれなかった古い Ready は破棄され
    /// droppedCount に計上される。何も due でなければ false。呼び出し側は読み終えたら必ず ReturnLease すること。
    /// </summary>
    public bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds, out RingFrame frame, out int droppedCount)
    {
        if (_seq.TryLeaseDue(clockPositionSeconds, frameDurationSeconds, out int idx, out double pts, out droppedCount))
        {
            var p = _payloads[idx];
            frame = new RingFrame(idx, p.Buffer, p.Width, p.Height, p.Stride, pts);
            return true;
        }
        frame = default;
        return false;
    }

    /// <summary>
    /// 最も古い Ready フレームを1枚リースする（クロック非依存。Step・一時停止中シーク用）。
    /// minSerial 未満の世代（＝シーク前の残骸）は対象外。timeout 内に無ければ false。
    /// </summary>
    public bool TryLeaseOldest(TimeSpan timeout, int minSerial, out RingFrame frame)
    {
        if (_seq.TryLeaseOldest(timeout, minSerial, out int idx, out double pts))
        {
            var p = _payloads[idx];
            frame = new RingFrame(idx, p.Buffer, p.Width, p.Height, p.Stride, pts);
            return true;
        }
        frame = default;
        return false;
    }

    /// <summary>リース中のスロットを Free に戻す。Close 済みで PendingFreeOnReturn なら、ここでネイティブバッファも解放する。</summary>
    public void ReturnLease(int slotIndex) => _seq.ReturnLease(slotIndex, FreePayload);

    /// <summary>
    /// シーク時: 世代番号を進めて Ready を Free に戻し、EOF 状態も解除する。どのスレッドから呼んでも安全
    /// （BeginWrite で空き待ち中のデコーダは SlotFlushed で起床する）。これにより「リング満杯でブロック中の
    /// デコードスレッドが FlushMarker を処理できない」デッドロック（後方シーク時に音声だけ流れて映像が止まる
    /// 不具合）を demux スレッド側から解消できる。
    /// </summary>
    public void Flush() => _seq.Flush();

    /// <summary>診断用: 全スロットの状態スナップショット（停止検知時のログ出力に使う）。</summary>
    public string DescribeSlots() => _seq.DescribeSlots();

    /// <summary>VideoDecodeThread が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof() => _seq.MarkEof();

    /// <summary>EOF 済みかつ表示待ち・リース中のフレームが残っていない（再生完了検出に使う）。</summary>
    public bool IsEofDrained => _seq.IsEofDrained;

    public void Close() => _seq.Close();

    private void FreePayload(int slotIndex)
    {
        var p = _payloads[slotIndex];
        if (p.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p.Buffer);
        p.Buffer = IntPtr.Zero;
        p.Capacity = 0;
    }

    /// <summary>
    /// Close 後に解放する。Leased 中のスロットは UI がまだ参照している可能性があるため即座には解放せず、
    /// PendingFreeOnReturn を立てて ReturnLease 時に解放する。
    /// </summary>
    public void Dispose()
    {
        Close();
        _seq.DisposeSlots(FreePayload);
    }
}
