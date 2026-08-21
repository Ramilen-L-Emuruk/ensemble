using Sdcb.FFmpeg.Raw;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>
/// <see cref="VideoDecodeThread"/>（producer）がデコード済みフレームをリングへ書き込むための抽象。
/// CPU 経路（<see cref="CpuFrameSink"/>: sws_scale）と GPU ゼロコピー経路（<see cref="GpuFrameSink"/>: VideoProcessor 変換）で
/// 変換手段が異なるため、書き込み戦略をここに集約する。<see cref="VideoDecodeThread"/> 側は preroll/世代制御に専念できる。
///
/// 使い方: <see cref="BeginWrite"/> → <see cref="WriteFrame"/> → 成否に応じて <see cref="CommitWrite"/> か <see cref="AbortWrite"/>。
/// スレッド契約: 単一のデコードスレッドから呼ぶ前提。
///
/// シーク時のリング Flush はこの抽象に含めない。demux スレッドがシークを実行した時点で
/// <see cref="MultiTrackPlayer.Engine.Video.IVideoFrameRing.Flush"/> を 1 回だけ呼ぶのが唯一の経路で、
/// デコード側は世代の一致しないフレームが <see cref="BeginWrite"/> で弾かれることに依存する。
/// </summary>
public unsafe interface IVideoFrameSink
{
    /// <summary>
    /// Free スロットが空くまでブロックして確保する。戻り値は slot 番号、または
    /// <c>SlotSequencer.SlotClosed</c>/<c>SlotSequencer.SlotFlushed</c>。
    /// </summary>
    /// <param name="epoch">
    /// 書き込むフレームを産んだパケットの世代。リングの現在世代と一致しなければ
    /// <c>SlotSequencer.SlotFlushed</c> が返る（シーク前の残骸フレームを弾く）。
    /// </param>
    int BeginWrite(int width, int height, SeekEpoch epoch);

    /// <summary>確保済みスロットへ frame を変換して書き込む。成功なら true（呼び出し側は <see cref="CommitWrite"/>）、失敗なら false（<see cref="AbortWrite"/>）。</summary>
    bool WriteFrame(AVFrame* frame, int slotIndex);

    /// <summary>書き込み完了。スロットを表示可能（Ready）にする。</summary>
    void CommitWrite(int slotIndex, double ptsSeconds);

    /// <summary>書き込みを破棄してスロットを Free に戻す。</summary>
    void AbortWrite(int slotIndex);

    /// <summary>EOF 受信・残フレーム drain 完了を通知する。</summary>
    void MarkEof();
}
