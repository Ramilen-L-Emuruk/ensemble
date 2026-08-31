using System.Runtime.InteropServices;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// GPU→CPU 転送後の BGRA フレームを保持するネイティブメモリの固定長リング。
/// VideoDecodeThread（producer）が Free スロットへ書き込み、呼び出し側（consumer）が
/// due なフレームをリースして直接読み取り、読み終えたら ReturnLease で返す（プル型・ゼロコピー）。
/// スロットの状態機械は <see cref="SlotSequencer"/> に委譲し、本クラスはネイティブバッファの確保・解放だけを担う。
/// </summary>
public sealed class VideoFrameRing : IVideoFrameRing
{
    private const int SlotCount = 4;

    /// <summary>BeginWrite の戻り値: Close 済み。</summary>
    public const int SlotClosed = SlotSequencer.SlotClosed;
    /// <summary>BeginWrite の戻り値: 書き込もうとしたフレームがすでに過去の世代（このフレームは破棄すべき）。</summary>
    public const int SlotFlushed = SlotSequencer.SlotFlushed;

    private sealed class Payload
    {
        public IntPtr Buffer = IntPtr.Zero;
        public int Capacity;
        public int Width, Height, Stride;
    }

    private readonly SlotSequencer _seq;
    private readonly Payload[] _payloads;

    public VideoFrameRing()
    {
        _payloads = new Payload[SlotCount];
        for (int i = 0; i < SlotCount; i++) _payloads[i] = new Payload();
        // _payloads を先に用意してから渡す（コールバックは後から呼ばれるが、順序を崩すと null 参照になる）
        _seq = new SlotSequencer(SlotCount, FreePayload);
    }

    public SeekEpoch CurrentEpoch => _seq.CurrentEpoch;

    /// <summary>
    /// Free スロットが空くまでブロックする。Close 済みなら SlotClosed、<paramref name="epoch"/> が
    /// 現在の世代と一致しなければ SlotFlushed。
    /// 確保できたスロットには幅×高さ×4 バイトのネイティブバッファを（必要なら作り直して）割り当てる。
    /// </summary>
    /// <param name="epoch">書き込むフレームを産んだパケットの世代（<see cref="SlotSequencer.BeginWrite"/> 参照）。</param>
    public int BeginWrite(int width, int height, SeekEpoch epoch)
    {
        int stride = width * 4;
        int needed = stride * height;
        return _seq.BeginWrite(epoch, idx =>
        {
            var p = _payloads[idx];
            if (p.Capacity < needed)
            {
                // 先に新しいバッファを確保し、成功してから旧バッファを解放する。逆順にすると
                // AllocHGlobal が OOM を投げたときに p.Buffer が解放済みポインタのまま残り、
                // 以後の FreePayload（リング破棄時）が同じ領域を二重解放する
                IntPtr fresh = Marshal.AllocHGlobal(needed);
                if (p.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p.Buffer);
                p.Buffer = fresh;
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
    public bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds, out VideoFrameLease? lease, out int droppedCount)
    {
        if (_seq.TryLeaseDue(clockPositionSeconds, frameDurationSeconds, out int idx, out double pts, out droppedCount))
        {
            var p = _payloads[idx];
            lease = new VideoFrameLease(idx, FrameKind.Cpu, p.Buffer, p.Width, p.Height, p.Stride,
                SharedSurfaceHandle: IntPtr.Zero, TimeSpan.FromSeconds(pts));
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
            lease = new VideoFrameLease(idx, FrameKind.Cpu, p.Buffer, p.Width, p.Height, p.Stride,
                SharedSurfaceHandle: IntPtr.Zero, TimeSpan.FromSeconds(pts));
            return true;
        }
        lease = null;
        return false;
    }

    /// <summary>リース中のスロットを Free に戻す。破棄時に遅延解放が予約されていれば、ここでネイティブバッファも解放する。</summary>
    public void ReturnLease(int slotIndex) => _seq.ReturnLease(slotIndex);

    /// <summary>
    /// シーク時: 世代を <paramref name="epoch"/> へ更新して Ready を Free に戻し、EOF 状態も解除する。
    /// どのスレッドから呼んでも安全（BeginWrite で空き待ち中のデコーダは SlotFlushed で起床する）。
    /// これにより「リング満杯でブロック中のデコードスレッドが Flush 番兵を処理できない」デッドロック
    /// （後方シーク時に音声だけ流れて映像が止まる不具合）を demux スレッド側から解消できる。
    /// 戻り値の意味は <see cref="SlotSequencer.Flush"/> を参照（false は呼び出し規約違反）。
    /// </summary>
    public bool Flush(SeekEpoch epoch) => _seq.Flush(epoch);

    /// <summary>Ready はあるがどれもまだ due でないか（<see cref="SlotSequencer.IsWaitingForFrameTime"/>）。</summary>
    public bool IsWaitingForFrameTime(double clockPositionSeconds, double frameDurationSeconds) =>
        _seq.IsWaitingForFrameTime(clockPositionSeconds, frameDurationSeconds);

    /// <summary>診断用: 全スロットの状態スナップショット（停止検知時のログ出力に使う）。</summary>
    public string DescribeSlots() => _seq.DescribeSlots();

    /// <summary>VideoDecodeThread が EOF 受信・残フレーム drain 完了後に呼ぶ。</summary>
    public void MarkEof() => _seq.MarkEof();

    /// <summary>EOF 済みかつ表示待ち（書き込み中・提示待ち）のフレームが残っていない。リース中は数えない（再生完了検出に使う）。</summary>
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
    /// Close 後に解放する。他スレッドがまだ参照している可能性があるスロット（UI がリース中・デコーダが
    /// 書き込み中）は即座には解放せず、参照が離れる時に解放する。
    /// </summary>
    public void Dispose()
    {
        Close();
        _seq.DisposeSlots();
    }
}
