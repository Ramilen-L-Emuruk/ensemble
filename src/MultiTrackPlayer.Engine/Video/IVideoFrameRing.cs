using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Engine.Video;

/// <summary>
/// フレームリングの読み出し・ライフサイクル面（consumer 側）の共通契約。CPU 経路（<see cref="VideoFrameRing"/>）と
/// GPU ゼロコピー経路（<see cref="GpuVideoFrameRing"/>）を <c>MediaEngine</c> から同一に扱うためのインターフェース。
///
/// 書き込み（producer）側の BeginWrite/CommitWrite/AbortWrite/MarkEof は経路ごとに変換手段が異なるため、
/// このインターフェースには含めず <see cref="MultiTrackPlayer.Engine.Pipeline.IVideoFrameSink"/> が担う。
/// リースは経路を問わず <see cref="VideoFrameLease"/>（<see cref="FrameKind"/> 付き）で返す。
/// </summary>
public interface IVideoFrameRing : IDisposable
{
    /// <summary>現在のシーク世代（demux が採番した値。<see cref="SeekEpoch"/> 参照）。</summary>
    SeekEpoch CurrentEpoch { get; }

    /// <summary>EOF 済みかつ表示待ち・リース中のフレームが残っていない（再生完了検出に使う）。</summary>
    bool IsEofDrained { get; }

    /// <summary>クロック位置に対して due な最新フレームを1枚リースする。何も due でなければ false。読み終えたら必ず <see cref="ReturnLease"/> すること。</summary>
    bool TryLeaseDue(double clockPositionSeconds, double frameDurationSeconds, out VideoFrameLease? lease, out int droppedCount);

    /// <summary>
    /// 最も古い Ready フレームを1枚リースする（クロック非依存。Step・一時停止中シーク用）。
    /// 世代が <paramref name="epoch"/> と一致するスロットだけを対象にする。timeout 内に無ければ false。
    /// </summary>
    bool TryLeaseOldest(TimeSpan timeout, SeekEpoch epoch, out VideoFrameLease? lease);

    /// <summary>リース中のスロットを Free に戻す。</summary>
    void ReturnLease(int slotIndex);

    /// <summary>
    /// シーク時: 世代を <paramref name="epoch"/> へ更新して Ready を Free に戻し EOF 状態も解除する
    /// （どのスレッドから呼んでも安全）。
    /// </summary>
    /// <returns>
    /// 適用した場合 true。現在と同じか古い世代で呼ばれて無視した場合 false
    /// （呼び出し規約違反。呼び出し側で記録すること）。
    /// </returns>
    bool Flush(SeekEpoch epoch);

    /// <summary>
    /// Ready なフレームを持っているが、どれもまだ due になっていない（＝次のフレームの時刻を
    /// 待っているだけで健全な）状態か。Ready が無い場合は false。
    /// 提示が止まっていることが異常かどうかの判断に使う（<c>MediaEngine.DetectVideoStall</c>）。
    /// </summary>
    bool IsWaitingForFrameTime(double clockPositionSeconds, double frameDurationSeconds);

    /// <summary>診断用: 全スロットの状態スナップショット。</summary>
    string DescribeSlots();

    /// <summary>これ以上の書き込みを止める（producer 停止時）。</summary>
    void Close();
}
