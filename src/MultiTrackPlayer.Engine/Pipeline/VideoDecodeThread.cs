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

    /// <summary>
    /// 「デコーダが前進しない」を何回連続で観測したらプリロールを完了扱いにするか。
    /// 単発の破損パケットでプリロールを解除してしまわないための下限。
    /// <para>
    /// 目的は音声側の <c>AudioDecodeThread.NoProgressAbandonThreshold</c> とは違う。あちらは
    /// トラックを畳んで他トラックを守るためで、こちらは<b>シーク後の出力保留が EOF まで
    /// 解けないのを防ぐため</b>（映像に「畳む」処理は無い）。値を同じ 50 にしてあるのは
    /// 揃えると読みやすいからで、数えている事実が違うので互いに追従する義務はない。
    /// </para>
    /// <para>
    /// 閾値へ達するまでの時間は通常ごく短い。前進しないパケットは変換もリング投入もせず捨てる
    /// だけなので、キューに積まれた分は数ミリ秒で消化する。ただし demux は単一スレッドで両方の
    /// キューへ振り分けるため、音声キューが詰まって <c>Put</c> でブロックしていれば映像パケットの
    /// 供給も止まり、その間は <c>_queue.Get</c> で実時間待つ（シーク直後は音声バッファを
    /// 空にした直後なので、この重なりは起きにくい）。
    /// </para>
    /// </summary>
    private const int NotProgressingPrerollThreshold = 50;

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

    // 異常が続くと毎フレーム記録されてログが埋まるため、最初の 1 回だけ残す。
    // フラグを分ける軸は 2 つある。ひとつは発生箇所（通常デコード中の HandlePacket と
    // EOF ドレインの HandleEof。片方の失敗が他方を隠さないようにする）。もうひとつは
    // 記録の強さで、常に残す規約違反（WriteFatal）と診断ログ限りの失敗（Write）を
    // 同じフラグで抑制すると、先に起きた軽い失敗が規約違反の記録を永久に打ち消す
    private bool _dataAfterDrainingLogged;
    private bool _eofFlushFailureLogged;
    private bool _eofFlushDoubleSendLogged;
    // 「このパケットは送れなかった」を連続で観測した回数（NoteNotProgressing が数える）。
    // 記録の抑制もこの値で行うため、専用フラグは置かない
    private int _notProgressingStreak;

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
            ReleasePendingPreroll("デコードスレッドの異常終了");
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
        _notProgressingStreak = 0;
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
        // AudioDecodeThread.HandleEof と同じ理由で戻り値を見る。ここが失敗すると後続の
        // TryReceiveFrame が空振りし、終端に残っていたフレームを出せないまま MarkEof するため、
        // 終端付近の映像が無言で欠ける。
        //
        // -EAGAIN の再送ループ（HandlePacket が持つもの）はここには置かない。あちらは 1 パケット
        // 送るごとに DrainAvailable で出力を吸い切ってから次へ進むので、この時点でデコーダの
        // 出力キューは空（前進しないため早期 return した場合も、その直前の DrainAvailable で
        // 空だったことを確認済み）。再送ループを足すと、EOF 経路に上限のない待ちを 1 つ
        // 増やすことになる（ここが止まると demux が Put でブロックして音声まで止まる）
        int flushRet = _decoder.SendPacket(null);
        // AVERROR_EOF は「既に draining 状態のデコーダへ flush パケットを送った」の意。
        // EOF 番兵は DemuxThread が _eofReached で 1 回に絞っており、_eofReached が false へ
        // 戻るのは PerformSeek の中だけ。そこは _videoQueue.Flush を必ず通るため、次の EOF 番兵
        // より前に Flush 番兵（HandleFlush の FlushBuffers）が挟まって draining 状態が落ちる。
        // つまりここは常に 1 回目のはず。返ってきたら呼び出し規約違反なので、
        // 診断ログの有効・無効に関わらず残す
        if (flushRet == AVERROR_EOF)
        {
            if (!_eofFlushDoubleSendLogged)
            {
                _eofFlushDoubleSendLogged = true;
                Diagnostics.DiagnosticLog.WriteFatal("video",
                    "EOF ドレインの flush パケットが二重送信された（終端付近の映像が欠ける）");
            }
        }
        else if (flushRet < 0 && !_eofFlushFailureLogged)
        {
            _eofFlushFailureLogged = true;
            Diagnostics.DiagnosticLog.Write("video",
                $"EOF ドレインの SendPacket が失敗 ret={flushRet}");
        }
        DrainAvailable(frame);
        ReleasePendingPreroll("EOF 到達");
        _sink.MarkEof();
    }

    /// <summary>
    /// 映像を一枚も出せないまま、シーク後のプリロールを完了扱いにして通知する。
    /// 目標地点へ届く映像が 1 枚も来ない場合（コンテナの duration が実際の最終フレームより
    /// 大きい・末尾が音声のみ・シーク自体が失敗した・デコーダが前進しない等）、ここで解除しないと
    /// ミキサーの出力保留が永久に解けず、音も映像も出ないまま固まる。
    /// （音声側の <c>AudioDecodeThread.CompletePrerollWithoutSamples</c> と対称）
    /// </summary>
    /// <remarks>
    /// 解除するとこのシーク世代では以後 <c>EmitFrame</c> のプリロールフィルタが働かず、
    /// 目標より前のフレームもリングへ流れる（次の Flush 番兵まで）。EOF 到達時は残りが無いので
    /// 差が出ないが、途中で解除した場合は目標より前の映像が提示対象になりうる。
    /// リングの due 判定で古いフレームは落ちるため実害は小さく、
    /// 出力保留が解けないまま固まることを避ける方を優先している。
    /// </remarks>
    /// <param name="reason">ログに残す理由（「EOF 到達」等）。</param>
    private void ReleasePendingPreroll(string reason)
    {
        if (!_prerollActive) return;
        _prerollActive = false;
        if (_queue.Epoch == _prerollEpoch)
        {
            Diagnostics.DiagnosticLog.Write("video", $"{reason}のためプリロールを完了扱いにする target={_prerollTarget:F3}");
            _onFirstFrameAfterFlush?.Invoke(_prerollEpoch);
        }
    }

    private void HandlePacket(AVPacket* pkt, AVFrame* frame)
    {
        int ret = _decoder.SendPacket(pkt);
        while (ret == -EAGAIN)
        {
            // -EAGAIN は「出力を吸い出すまで入力を受け付けない」の意。1 枚も吸えていないのに
            // 再送してもデコーダの状態は変わらないため、前進を確かめずに回すとこのループは
            // CPU を焼きながら永久に抜けない。そうなると停止待ち（MediaEngine.JoinOrLog）が
            // 3 秒で諦め、キュー・リング・変換器が検疫されて「ファイルを開き直すまで再生が
            // 再開しない」状態になる。DrainAvailable は停止要求中は何も吸わないので、
            // 停止操作・ファイル切替のたびにこの窓を踏みうる
            bool drained = DrainAvailable(frame);
            // 停止要求で吸うのをやめた場合。パイプラインを畳んでいる最中の正常な経路なので記録しない
            if (_stopRequested) return;
            if (!drained)
            {
                // -EAGAIN on send は「まず receive せよ」の意なので、その直後に 1 枚も受け取れない
                // のは規約上想定されない状態。ただし TryReceiveFrame は -EAGAIN・AVERROR_EOF・
                // 本物のデコードエラーをまとめて false にするため、破損パケット由来の単発エラーも
                // ここへ来る。そのためこのパケットを捨てるだけに留め、次のパケットで回復させる
                NoteNotProgressing("デコーダが入力を受け付けず出力も出せない");
                return;
            }
            ret = _decoder.SendPacket(pkt);
        }
        // AVERROR_EOF は「draining 状態のデコーダへ通常パケットを送った」の意。EOF 番兵の後に
        // Flush 番兵を挟まず Data パケットが来たことになり、HandleEof の二重送信と同じ規約違反
        //（DemuxThread は EOF 後、PerformSeek で Flush してからしか新しいパケットを流さない）
        // 連続回数には数えない。この状態なら HandleEof が既に走って MarkEof しており、
        // 映像が出ないこと自体は再生完了として扱われる（畳む相当の手当ては済んでいる）
        if (ret == AVERROR_EOF)
        {
            if (!_dataAfterDrainingLogged)
            {
                _dataAfterDrainingLogged = true;
                Diagnostics.DiagnosticLog.WriteFatal("video",
                    "draining 状態のデコーダへ通常パケットを送った（Flush 番兵を挟まず Data が届いた）");
            }
        }
        else if (ret < 0)
        {
            // 送信が失敗し続けると -EAGAIN で詰まるのと結末が同じ（フレームが 1 枚も出ない）。
            // 症状が同じなので同じ数え方に載せる。TryReceiveFrame 側は失敗をログするのに
            // SendPacket だけ無言だと、映像が止まったときに手がかりが残らないため、
            // 記録も NoteNotProgressing に任せる（初回の 1 行に ret を載せている）
            NoteNotProgressing($"SendPacket が失敗した（ret={ret}）");
        }
        else
        {
            // パケットが受け付けられた＝前進した。連続回数を数え直す
            _notProgressingStreak = 0;
        }
        DrainAvailable(frame);
    }

    /// <summary>
    /// このパケットをデコーダへ送れなかったことを記録し、連続が閾値に達したら
    /// シーク後のプリロールを完了扱いにする。
    /// <para>
    /// 解除が要る理由: プリロール中に前進しなくなると完了通知が出ないままになり、ミキサーの
    /// 出力保留が EOF に達するまで解けない（音も映像も出ない時間が残り再生時間ぶん続く）。
    /// 目標より前の映像が出うることは受け入れて、保留を解く方を選ぶ。
    /// </para>
    /// <para>
    /// 記録の強さは音声側（<c>AudioDecodeThread.NoteNotProgressing</c>）と揃えてある。
    /// 初回は破損パケット 1 個かもしれないので診断ログ限り、閾値に達して
    /// 「このシークでは復帰しない」と確定した時点で <c>WriteFatal</c>。
    /// 逆にすると fatal.log 上で「1 回コケてすぐ回復した」と「以後ずっと固まった」が区別できない。
    /// </para>
    /// <para>
    /// 対象は<b>送信できなかった</b>場合だけで、「送信は通ったのに
    /// <c>avcodec_receive_frame</c> が本物のエラーを返し続ける」経路は数えていない。
    /// 送信成功時にフレームが出ないのはリオーダ遅延で正常に起きるため、枚数では区別できず、
    /// <c>TryReceiveFrame</c> が -EAGAIN・AVERROR_EOF・本物のエラーを <c>false</c> に畳んでいる
    /// 契約を変えないと捕まえられない（デバイス喪失からの復旧と同じ領域なので切り離してある）。
    /// </para>
    /// <para>
    /// なお映像側には利用者へ知らせる経路が無い（<see cref="AbandonVideoPipeline"/> も同様）ため、
    /// 恒久的に前進しなくなった場合は「音だけ進んで映像が固まる」形で現れる。
    /// </para>
    /// </summary>
    /// <param name="reason">ログに残す理由。</param>
    private void NoteNotProgressing(string reason)
    {
        // 理由は括弧に入れて渡す。テンプレートの地の文へ直に埋めると reason の文末
        //（体言か述語か）に文法が依存し、呼び出し側を増やすたびに日本語が壊れる
        _notProgressingStreak++;
        if (_notProgressingStreak == 1)
        {
            Diagnostics.DiagnosticLog.Write("video",
                $"パケットを破棄（{reason}）。連続 {NotProgressingPrerollThreshold} 回で"
                + "プリロールを完了扱いにする");
        }
        else if (_notProgressingStreak == NotProgressingPrerollThreshold)
        {
            // ここへ来たら「このシークでは映像が復帰しない」と見てよい。既定運用でも残す
            Diagnostics.DiagnosticLog.WriteFatal("video",
                $"前進しない状態が {NotProgressingPrerollThreshold} 回連続した（{reason}）。"
                + "このシークでは映像が出ない。音声は進む");
            ReleasePendingPreroll("デコーダの前進不能");
        }
    }

    /// <summary>デコーダの出力を吸い出してリングへ流す。</summary>
    /// <returns>1 枚以上取り出した場合 true。停止要求中、またはデコードエラーで 1 枚も
    /// 取り出せなかった場合 false（呼び出し側は「再送しても前進しない」と判断する）。</returns>
    private bool DrainAvailable(AVFrame* frame)
    {
        // 停止要求後はフレームを取り出して処理しない（EmitFrame → GPU テクスチャ生成に入らせない）。
        bool drainedAny = false;
        while (!_stopRequested && _decoder.TryReceiveFrame(frame))
        {
            drainedAny = true;
            EmitFrame(frame);
            av_frame_unref(frame);
        }
        return drainedAny;
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
