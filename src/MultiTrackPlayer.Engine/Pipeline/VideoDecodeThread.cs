using MultiTrackPlayer.Engine.Decoding;
using MultiTrackPlayer.Engine.Video;
using Sdcb.FFmpeg.Raw;
using System.Linq;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>映像codec ctxを専有し、パケットキューから受け取ってプリロール判定・変換・リング投入までを行う。</summary>
public sealed unsafe class VideoDecodeThread
{
    private readonly VideoDecoder _decoder;
    private readonly VideoPacketQueue _queue;
    private readonly IVideoFrameSink _sink;
    private readonly Func<double> _getPtsSyncOffset;
    private readonly double _frameDurationSeconds;
    private readonly Action<SeekEpoch>? _onFirstFrameAfterFlush;

    private volatile bool _stopRequested;
    private readonly object _seekTargetLock = new();
    // シーク世代をキーに目標値を対応付ける。FIFO（順序のみで対応付け）だと、
    // BoundedSerialQueue.Flush() が短時間に連続で呼ばれた際に前の Flush 番兵が Clear() で
    // 消えて後続の1個しか生き残らない一方、SetSeekTarget は呼ばれた回数だけ積まれてしまい、
    // 生き残った番兵が本来とは別のシークの目標値を取り出してしまうバグがあった
    // （ほぼ同時刻の2連続シークだけで再生が固まる不具合の原因）
    private readonly Dictionary<SeekEpoch, double> _pendingSeekTargets = new();

    /// <summary>
    /// 最後に処理した Flush 番兵の世代。キューの世代と一致していればシークに追いついている。
    /// <c>SeekEpoch</c> は構造体で volatile にできないため、内部では int で保持して
    /// <see cref="Volatile"/> で読み書きする（保持しているのは世代番号そのもの）。
    /// </summary>
    public SeekEpoch HandledEpoch => new(Volatile.Read(ref _handledEpochValue));
    private int _handledEpochValue;

    private bool _prerollActive;
    private double _prerollTarget;
    // このプリロールが属するシーク世代（Flush 番兵の世代）。プリロール完了判定の瞬間に
    // 現在のキュー世代と比較することで、既に次のシークに割り込まれた「無効な世代の完了通知」を検出する
    private SeekEpoch _prerollEpoch = SeekEpoch.Initial;
    // このスレッドが現在処理しているデータの世代。Flush 番兵で更新され、リングへの書き込み時に
    // スロットへ刻まれる。demux がリングを Flush した後もこのスレッドはしばらく前の世代のパケットを
    // 処理し続けるため、「リングの現在世代」ではなくこの値を渡すことで残骸フレームを弾く
    private SeekEpoch _epoch = SeekEpoch.Initial;
    // BeginWrite が SlotFlushed を返した（＝世代が変わった）後、Flush 番兵に到達するまでの
    // 残りフレームは全てシーク前の残骸なので、4K変換を行わずに捨てる
    private bool _abandonUntilFlush;

    public VideoDecodeThread(VideoDecoder decoder, VideoPacketQueue queue, IVideoFrameSink sink,
        Func<double> getPtsSyncOffset, double frameDurationSeconds, Action<SeekEpoch>? onFirstFrameAfterFlush = null)
    {
        _decoder = decoder;
        _queue = queue;
        _sink = sink;
        _getPtsSyncOffset = getPtsSyncOffset;
        _frameDurationSeconds = frameDurationSeconds;
        _onFirstFrameAfterFlush = onFirstFrameAfterFlush;
    }

    /// <summary>
    /// DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと（happens-before の担保に必要）。
    /// <paramref name="epoch"/> は DemuxThread が採番した世代で、この後に投入される Flush 番兵が同じ値を持つ。
    /// </summary>
    public void SetSeekTarget(SeekEpoch epoch, double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _pendingSeekTargets[epoch] = normalizedTargetSeconds;
    }

    public void RequestStop() => _stopRequested = true;

    public void Run()
    {
        AVFrame* frame = av_frame_alloc();
        try
        {
            while (!_stopRequested)
            {
                if (!_queue.Get(out var item)) break; // Close 済み

                switch (item.Kind)
                {
                    case QueueItemKind.Flush:
                        HandleFlush(item.Epoch);
                        break;
                    case QueueItemKind.Eof:
                        HandleEof(frame);
                        break;
                    case QueueItemKind.Data:
                        var pkt = (AVPacket*)item.Value;
                        try { HandlePacket(pkt, frame); }
                        finally { PacketOwnership.Release(pkt); }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // デコード／GPU 相互運用で想定外の例外が出ても、専用スレッドの未処理例外として
            // プロセス全体を fail-fast で巻き込まない（連続ファイル切替時の破棄競合など）。
            // 握り潰しではなく異常は必ず記録し、このスレッドは安全に終了する。
            // Write は診断ログ無効時（既定）に何もしないため、ここは WriteFatal を使う
            //（この経路を無記録にすると「映像だけ静かに止まる」原因不明の不具合になる）
            Diagnostics.DiagnosticLog.WriteFatal("video", $"デコードスレッド異常終了（以降の映像処理を停止）: {ex}");
            AbandonVideoPipeline();
        }
        finally
        {
            av_frame_free(&frame);
        }
    }

    /// <summary>
    /// このスレッドが異常終了するときの後始末（<c>AudioDecodeThread.AbandonAudioPipeline</c> と対称）。
    /// 映像側の待ち合わせを解かずに消えると、次の 3 つが同時に止まる:
    /// ・プリロールゲートが解除されず、ミキサーの音声出力保留が永久に続く
    /// ・映像リングが EOF にならず、再生完了が検出されない（次のファイルへ進めない）
    /// ・誰も引き取らない映像キューが満杯になり、AVFormatContext を専有する demux スレッドが
    ///   Put でブロックして音声の供給まで止まる（＝全パイプラインの凍結）
    /// キューを閉じても demux は停止しない（停止判定は停止要求の有無で行う）ため、
    /// 音声だけの再生が終端まで続き、そこで再生完了が正しく検出される。
    /// </summary>
    private void AbandonVideoPipeline()
    {
        try
        {
            ReleasePendingPreroll();
            _sink.MarkEof();
            _queue.Close();
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticLog.WriteFatal("video", $"映像パイプラインの後始末に失敗: {ex}");
        }
    }

    private void HandleFlush(SeekEpoch epoch)
    {
        // キューの世代と突き合わせて「このスレッドがシークに追いついたか」を外から判断できるようにする
        Volatile.Write(ref _handledEpochValue, epoch.Value);
        _epoch = epoch;
        _decoder.FlushBuffers();
        // リングの Flush はここでは呼ばない。demux スレッドがシークを実行した時点で 1 回だけ
        // 呼んでおり（リング満杯でこのスレッドがブロックしていても解けるように）、
        // その後にコミットされ得た残骸フレームは BeginWrite の世代照合で弾かれる。
        // ここで重ねて呼ぶと、新世代として正しく書き込まれたフレームまで掃除してしまう
        _abandonUntilFlush = false;
        _prerollEpoch = epoch;
        lock (_seekTargetLock)
        {
            _prerollActive = _pendingSeekTargets.Remove(epoch, out _prerollTarget);
            if (!_prerollActive) _prerollTarget = double.NaN;
            // この番兵より前の世代宛ての目標は、対応する番兵が Flush() の Clear() で
            // 消えて二度と来ない残骸。溜め続けると意味のない対応関係が残るので掃除する
            if (_pendingSeekTargets.Count > 0)
            {
                foreach (SeekEpoch staleKey in _pendingSeekTargets.Keys.Where(k => k <= epoch).ToList())
                    _pendingSeekTargets.Remove(staleKey);
            }
        }
        Diagnostics.DiagnosticLog.Write("video", $"flush 処理 epoch={epoch} preroll={( _prerollActive ? _prerollTarget.ToString("F3") : "なし")}");
    }

    private void HandleEof(AVFrame* frame)
    {
        _decoder.SendPacket(null);
        DrainAvailable(frame);
        ReleasePendingPreroll();
        _sink.MarkEof();
    }

    /// <summary>
    /// プリロール中のままファイル終端に達したときに、完了扱いにして通知する。
    /// 目標地点へ届く映像が 1 枚も来ない場合（コンテナの duration が実際の最終フレームより
    /// 大きい・末尾が音声のみ・シーク自体が失敗した等）、ここで解除しないと
    /// ミキサーの出力保留が永久に解けず、音も映像も出ないまま固まる。
    /// </summary>
    private void ReleasePendingPreroll()
    {
        if (!_prerollActive) return;
        _prerollActive = false;
        if (_queue.Epoch == _prerollEpoch)
        {
            Diagnostics.DiagnosticLog.Write("video", $"EOF 到達のためプリロールを完了扱いにする target={_prerollTarget:F3}");
            _onFirstFrameAfterFlush?.Invoke(_prerollEpoch);
        }
    }

    private void HandlePacket(AVPacket* pkt, AVFrame* frame)
    {
        int ret = _decoder.SendPacket(pkt);
        while (ret == -EAGAIN)
        {
            DrainAvailable(frame);
            ret = _decoder.SendPacket(pkt);
        }
        DrainAvailable(frame);
    }

    private void DrainAvailable(AVFrame* frame)
    {
        // 停止要求後はフレームを取り出して処理しない（EmitFrame → GPU テクスチャ生成に入らせない）。
        while (!_stopRequested && _decoder.TryReceiveFrame(frame))
        {
            EmitFrame(frame);
            av_frame_unref(frame);
        }
    }

    private void EmitFrame(AVFrame* frame)
    {
        if (_abandonUntilFlush) return; // シーク発生後の残骸フレーム（FlushMarker 到達まで捨てる）

        double offset = _getPtsSyncOffset();
        if (double.IsNaN(offset)) return; // demux 側で確定前（通常は先に確定している）

        double normalizedPts = _decoder.GetPtsSeconds(frame) - offset;

        if (_prerollActive)
        {
            if (normalizedPts < _prerollTarget - _frameDurationSeconds / 2.0)
                return; // hw転送・sws変換前に破棄（4Kの33MB転送を丸ごと省く）

            // プリロール完了と判定できたが、その間に次のシークが割り込んでキューの世代が
            // 既に進んでいる場合、これは無効な世代の完了通知（このデコードスレッドがまだ
            // 新しい Flush 番兵に到達していないだけ）。コールバックを発火せず残骸として捨てる。
            // 発火してしまうと MediaEngine 側が古いシーク目標を「映像プリロール完了」と誤認し、
            // 音声側が別世代で先に完了していた場合にミキサーの保留を誤って解除してしまう
            // （巻き戻し連打時に稀に発生する早送り/大量ドロップの原因）
            if (_queue.Epoch != _prerollEpoch)
            {
                _abandonUntilFlush = true;
                Diagnostics.DiagnosticLog.Write("video", $"stale preroll 破棄 prerollEpoch={_prerollEpoch} currentEpoch={_queue.Epoch} pts={normalizedPts:F3}");
                return;
            }

            _prerollActive = false;
            Diagnostics.DiagnosticLog.Write("video", $"preroll 完了 firstPts={normalizedPts:F3} epoch={_prerollEpoch}");
            // シーク後、映像プリロールがここで完了する。MediaEngine 側はこれを合図に
            // ミキサーの音声出力保留（HoldOutput）を解除する（早送りバグの根治）
            _onFirstFrameAfterFlush?.Invoke(_prerollEpoch);
        }

        // 停止要求後は新規スロットの GPU テクスチャ生成（CreateSlotTexture）に入らない。
        // Teardown（RequestStop → ring.Close → Join）と破棄済み D3D リソースへのアクセスを競合させないため。
        if (_stopRequested) return;

        // リングの現在世代ではなく、このフレームを産んだパケットの世代を渡す。
        // demux が既に次の世代へリングを Flush していれば SlotFlushed が返り、残骸として捨てられる
        int slot = _sink.BeginWrite(frame->width, frame->height, _epoch);
        if (slot == SlotSequencer.SlotClosed) return;
        if (slot == SlotSequencer.SlotFlushed)
        {
            // シークごとに数枚は必ず通る正常系（demux が先にリングを Flush するため）。
            // 上の stale preroll 破棄と同じ「世代不一致でフレームを捨てた」事象なので記録も対称にする。
            // 正常系なので Write（既定 no-op）で十分。世代の渡し方を誤ってここが延々と続く状態に
            // なった場合は「映像が一切出ない」という明白な症状で露見するため、
            // 連続回数を数えて WriteFatal へ昇格させる仕組みは置いていない
            _abandonUntilFlush = true;
            Diagnostics.DiagnosticLog.Write("video",
                $"世代不一致でフレーム破棄 frameEpoch={_epoch} pts={normalizedPts:F3}");
            return;
        }

        // 変換手段（CPU sws_scale / GPU VideoProcessor）は sink 実装に委譲する。
        // 確保したスロットは、例外が飛んでも必ず Commit か Abort のどちらかで手放すこと。
        // Writing のまま抜けると、リング破棄時に「まだ書き込み中」と見なされてペイロードが
        // 遅延解放の予約に回り、このスレッドが戻ってこないぶんだけ恒久リークになる
        bool committed = false;
        try
        {
            if (_sink.WriteFrame(frame, slot))
            {
                _sink.CommitWrite(slot, normalizedPts);
                committed = true;
            }
        }
        finally
        {
            if (!committed) _sink.AbortWrite(slot);
        }
    }
}
