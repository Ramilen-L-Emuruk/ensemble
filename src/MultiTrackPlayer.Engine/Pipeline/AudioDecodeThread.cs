using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
using Sdcb.FFmpeg.Raw;
using System.Linq;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>全音声トラックのデコードを単一スレッドで駆動する。preroll・充填ゲート・EOF ドレインを担う。</summary>
public sealed unsafe class AudioDecodeThread
{
    private static readonly TimeSpan FillGateThreshold = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan FillGatePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IReadOnlyList<AudioDecoder> _decoders;
    private readonly IReadOnlyList<AudioTrackState> _states;
    private readonly AudioPacketQueue _queue;
    private readonly Func<double> _getPtsSyncOffset;
    private readonly Action<double>? _onFirstSamplesAfterFlush;
    private readonly Action<int>? _onTrackAbandoned;
    private readonly ManualResetEventSlim _wake = new(false);

    private volatile bool _stopRequested;
    private readonly object _seekTargetLock = new();
    // VideoDecodeThread と同じ理由（コメント参照）で FIFO ではなくシーク世代キー付き辞書にする
    private readonly Dictionary<SeekEpoch, double> _pendingSeekTargets = new();
    private bool _prerollActive;
    private double _prerollTarget;
    // このプリロールが属するシーク世代（Flush 番兵の世代）。VideoDecodeThread と同じ理由で、
    // 次のシークに割り込まれた後の「無効な世代のプリロール完了」による誤アンカーを防ぐために使う
    private SeekEpoch _prerollEpoch = SeekEpoch.Initial;
    private bool _anchorNotifyPending;
    private double _anchorTarget;

    /// <summary>
    /// 最後に処理した Flush 番兵の世代。キューの世代と一致していればシークに追いついている。
    /// <c>SeekEpoch</c> は構造体で volatile にできないため、内部では int で保持して
    /// <see cref="Volatile"/> で読み書きする（<c>VideoDecodeThread.HandledEpoch</c> と対称）。
    /// </summary>
    public SeekEpoch HandledEpoch => new(Volatile.Read(ref _handledEpochValue));
    private int _handledEpochValue;

    // 異常が続くと毎フレーム記録されてログが埋まるため、トラックごとに最初の 1 回だけ残す
    private readonly bool[] _resampleFailureLogged;
    private readonly bool[] _sendPacketFailureLogged;
    // EOF ドレインの flush パケット失敗。通常デコード中の失敗（_sendPacketFailureLogged）とは
    // 原因もタイミングも別なので、抑制フラグを分けて片方が他方を隠さないようにする
    private readonly bool[] _eofFlushFailureLogged;

    private readonly ResampleFailureTracker _resampleFailures;

    /// <summary>
    /// リサンプルできず切り離したトラック。以降このトラックのデコード結果はバッファへ足さない
    /// （<c>AudioTrackState.IsEof</c> を立てた状態とバッファ内容の整合を保つため）。
    /// シーク時の Flush で一旦解除するが、リサンプラ自体が壊れているトラックは
    /// シーク後の最初のフレームで再び切り離される。
    /// </summary>
    private readonly bool[] _abandonedTracks;

    // 切り離しはシークごとに再発生しうるため、記録と通知はトラックごとに最初の 1 回だけに絞る
    private readonly bool[] _abandonLogged;

    /// <exception cref="ArgumentException">decoders と states の件数が一致しない場合。</exception>
    public AudioDecodeThread(
        IReadOnlyList<AudioDecoder> decoders, IReadOnlyList<AudioTrackState> states,
        AudioPacketQueue queue, Func<double> getPtsSyncOffset,
        Action<double>? onFirstSamplesAfterFlush = null,
        Action<int>? onTrackAbandoned = null)
    {
        // 両者は常に同じトラック番号で添字アクセスされる。不一致のまま走らせると
        // デコード中に IndexOutOfRangeException となりスレッドごと停止する
        if (decoders.Count != states.Count)
            throw new ArgumentException(
                $"デコーダ数（{decoders.Count}）とトラック状態数（{states.Count}）が一致しない", nameof(states));

        _decoders = decoders;
        _states = states;
        _queue = queue;
        _getPtsSyncOffset = getPtsSyncOffset;
        _onFirstSamplesAfterFlush = onFirstSamplesAfterFlush;
        _onTrackAbandoned = onTrackAbandoned;
        _resampleFailureLogged = new bool[decoders.Count];
        _sendPacketFailureLogged = new bool[decoders.Count];
        _eofFlushFailureLogged = new bool[decoders.Count];
        _resampleFailures = new ResampleFailureTracker(decoders.Count);
        _abandonedTracks = new bool[decoders.Count];
        _abandonLogged = new bool[decoders.Count];
    }

    /// <summary>
    /// DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと。
    /// <paramref name="epoch"/> は DemuxThread が採番した世代で、この後に投入される Flush 番兵が同じ値を持つ。
    /// </summary>
    public void SetSeekTarget(SeekEpoch epoch, double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _pendingSeekTargets[epoch] = normalizedTargetSeconds;
    }

    public void RequestStop() => _stopRequested = true;

    /// <summary>充填ゲート待ち（Pause 起因のバックプレッシャー含む）を起こす。ミキサーの Read 完了時・シャットダウン時に呼ぶ。</summary>
    public void Wake() => _wake.Set();

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
                        var reference = item.Value;
                        var pkt = (AVPacket*)reference.Packet;
                        try { HandlePacket(reference.TrackIndex, pkt, frame); }
                        finally { PacketOwnership.Release(pkt); }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // VideoDecodeThread.Run と同じ理由（そちらのコメント参照）。.NET では非 UI スレッドの
            // 未処理例外がプロセス即終了に直結するため、音声 1 トラックの障害でアプリ全体を巻き込まない。
            // デバッグモードが無効でも原因を追えるよう WriteFatal で残す。
            Diagnostics.DiagnosticLog.WriteFatal("audio", $"音声デコードスレッド異常終了（以降の音声は出力されない）: {ex}");
            AbandonAudioPipeline();
        }
        finally
        {
            av_frame_free(&frame);
        }
    }

    /// <summary>
    /// このスレッドが異常終了したあと、再生全体が固まらないように音声側を畳む。
    /// ここを怠ると次の 2 つの経路で映像ごと停止する:
    /// ・IsEof が false のままバッファが空になったトラックがあると、ミキサーの共通利用可能量が 0 に
    ///   固定され、健全なトラックまで無音になったうえクロックが進まなくなる
    /// ・誰も Get() しなくなったキューがやがて満杯になり、映像と音声を同一スレッドで読む
    ///   DemuxThread が Put でブロックして映像の供給まで止まる
    /// </summary>
    private void AbandonAudioPipeline()
    {
        try
        {
            foreach (var state in _states) state.IsEof = true;
            _queue.Close();
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticLog.WriteFatal("audio", $"音声パイプラインの後始末に失敗: {ex}");
        }
    }

    // 同じ異常が毎フレーム続いてもログが埋まらないよう、トラックごとに最初の 1 回だけ記録する
    private static void LogOnce(bool[] logged, int trackIndex, string message)
    {
        if (trackIndex < 0 || trackIndex >= logged.Length || logged[trackIndex]) return;
        logged[trackIndex] = true;
        Diagnostics.DiagnosticLog.Write("audio", message);
    }

    private void HandleFlush(SeekEpoch epoch)
    {
        // キューの世代と突き合わせて「このスレッドがシークに追いついたか」を外から判断できるようにする
        Volatile.Write(ref _handledEpochValue, epoch.Value);
        // 連続失敗で切り離したトラックには復帰の機会を与える（シーク先では正常にリサンプルできる
        // 場合がある）。リサンプラ自体が壊れているトラック（AudioDecoder.IsResamplerBroken）は
        // シークでは回復しないため、シーク後の最初のフレームで再び切り離される
        _resampleFailures.Reset();
        for (int i = 0; i < _decoders.Count; i++)
        {
            _decoders[i].FlushBuffers();
            _states[i].Buffer.ClearBuffer();
            _states[i].IsEof = false;
            _abandonedTracks[i] = false;
        }
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
        // クロックの錨（anchor）はシーク後最初の「新しい」音声サンプル投入時に要求する。
        // UI の Seek() 時点で要求すると、ミキサーに残る旧位置の音声で錨が早期消費されて
        // クロックとA/Vが恒久的にズレる（実機検証で -56s のズレとして観測されたバグ）。
        _anchorNotifyPending = _prerollActive;
        _anchorTarget = _prerollTarget;
        Diagnostics.DiagnosticLog.Write("audio", $"flush 処理 epoch={epoch} preroll={(_prerollActive ? _prerollTarget.ToString("F3") : "なし")}");
    }

    private void HandleEof(AVFrame* frame)
    {
        // プリロール中のまま終端に達すると完了通知が出ず、ミキサーの出力保留が解けない
        CompletePrerollWithoutSamples("EOF 到達");
        for (int i = 0; i < _decoders.Count; i++)
        {
            // HandlePacket 側と同じ理由で戻り値を見る。ここが失敗すると後続の TryReceiveFrame が
            // 空振りし、終端に残っていたフレームを出せないまま IsEof を立てるため、終端付近の音が
            // 無言で欠ける。抑制フラグを HandlePacket と分けているのは、通常デコード中の失敗が
            // 先に記録されていると EOF ドレイン特有の失敗が隠れて残らなくなるため。
            //
            // -EAGAIN の再送ループ（HandlePacket が持つもの）はここには置かない。あちらは
            // 1 パケット送るごとに DrainInto で必ず出力を吸い切ってから次へ進むので、この時点で
            // デコーダの出力キューは空。再送ループを足すと、EOF 経路に上限のない待ちを
            // 1 つ増やすことになる（ここが止まると demux が Put でブロックして映像まで止まる）
            int flushRet = _decoders[i].SendPacket(null);
            if (flushRet < 0 && !_eofFlushFailureLogged[i])
            {
                _eofFlushFailureLogged[i] = true;
                // AVERROR_EOF は「既に draining 状態のデコーダへ flush パケットを送った」の意。
                // EOF 番兵は DemuxThread が _eofReached で 1 回に絞り、次の EOF までに必ず
                // Flush 番兵（HandleFlush の FlushBuffers）が挟まるため、ここは常に 1 回目のはず。
                // 返ってきたら呼び出し規約違反なので、診断ログの有効・無効に関わらず残す
                if (flushRet == AVERROR_EOF)
                    Diagnostics.DiagnosticLog.WriteFatal("audio",
                        $"EOF ドレインの flush パケットが二重送信された track={i}（終端付近の音が欠ける）");
                else
                    Diagnostics.DiagnosticLog.Write("audio",
                        $"EOF ドレインの SendPacket が失敗 track={i} ret={flushRet}");
            }
            while (_decoders[i].TryReceiveFrame(frame))
            {
                var pcm = _decoders[i].ResampleFrame(frame);
                if (pcm != null) AddWithGate(i, pcm, 0, pcm.Length);
                else LogOnce(_resampleFailureLogged, i, $"EOF ドレイン中のリサンプルに失敗 track={i}");
                av_frame_unref(frame);
            }
            // デコードエラーで抜けた場合も EOF として扱う。ここを立てないと、
            // ミキサーの ComputeCommonAvailableBytes がこのトラックを除外できず
            // 健全な他トラックまで巻き込んで無音になる
            _states[i].IsEof = true;
        }
    }

    /// <summary>
    /// 音声サンプルを一つも出せないまま、シーク後のプリロールを完了扱いにする。
    /// これを怠るとミキサーの出力保留（HoldOutput）が解除されず、映像ごと止まる。
    /// </summary>
    /// <param name="reason">ログに残す理由（「EOF 到達」等）。</param>
    private void CompletePrerollWithoutSamples(string reason)
    {
        if (!_prerollActive && !_anchorNotifyPending) return;

        double target = _prerollTarget;
        // AddWithGate と同じ理由の世代チェック: 次のシークに割り込まれていたら、この完了通知は
        // もう無効な世代のもの（本物の Flush が後から来て正しく上書きする）
        bool epochMatches = _queue.Epoch == _prerollEpoch;
        _prerollActive = false;
        _anchorNotifyPending = false;
        if (!epochMatches) return;

        Diagnostics.DiagnosticLog.Write("audio", $"{reason}のためプリロールを完了扱いにする target={target:F3}");
        _onFirstSamplesAfterFlush?.Invoke(target);
    }

    /// <summary>
    /// リサンプル失敗を記録し、回復の見込みがないトラックを切り離す。リサンプラ自体が壊れている
    /// 場合は同じフレーム形式で再試行しても直らないため即座に、それ以外（<c>swr_convert</c> が
    /// 負値を返す一時的な失敗）は連続失敗が閾値に達してから切り離す。
    /// </summary>
    private void HandleResampleFailure(int trackIndex, AudioDecoder decoder)
    {
        if (decoder.IsResamplerBroken)
        {
            if (!_abandonedTracks[trackIndex])
                AbandonTrack(trackIndex, "リサンプラの初期化に失敗した");
            return;
        }
        if (_resampleFailures.RecordFailure(trackIndex))
            AbandonTrack(trackIndex, $"リサンプルが {_resampleFailures.Threshold} 回連続で失敗した");
    }

    /// <summary>
    /// リサンプルできないトラックを EOF 扱いにして切り離す。ミキサーは「EOF かつバッファ残量ゼロ」の
    /// トラックだけを共通利用可能量の計算から除外するため、これを立てないと当該トラックの残量ゼロが
    /// 全体を 0 に固定し、健全な他トラックまで無音になったうえクロックも進まなくなる。
    /// </summary>
    private void AbandonTrack(int trackIndex, string reason)
    {
        _abandonedTracks[trackIndex] = true;
        _states[trackIndex].IsEof = true;
        // シークのたびに同じトラックが再び切り離されるため、記録と通知は最初の 1 回だけにする
        //（連続シークで fatal.log へ積み続けると、本当に重要な記録の見通しが悪くなる）
        if (!_abandonLogged[trackIndex])
        {
            _abandonLogged[trackIndex] = true;
            Diagnostics.DiagnosticLog.WriteFatal("audio",
                $"音声トラックを切り離した track={trackIndex}（他トラックの再生は継続する）: {reason}");
            // 記録だけでは「なぜか片方のトラックが無音になった」理由がユーザーに伝わらない
            _onTrackAbandoned?.Invoke(trackIndex);
        }

        // 全トラックを切り離すと、この先バッファへの追加（AddWithGate）に到達しないため、
        // シーク後のプリロール完了通知が出ずミキサーの出力保留が永久に解けない
        if (_abandonedTracks.All(abandoned => abandoned))
            CompletePrerollWithoutSamples("全音声トラックの切り離し");
    }

    private void HandlePacket(int trackIndex, AVPacket* pkt, AVFrame* frame)
    {
        if (trackIndex < 0 || trackIndex >= _decoders.Count)
        {
            Diagnostics.DiagnosticLog.Write("audio",
                $"範囲外のトラック番号のパケットを破棄 trackIndex={trackIndex} decoderCount={_decoders.Count}");
            return;
        }
        var decoder = _decoders[trackIndex];

        int ret = decoder.SendPacket(pkt);
        while (ret == -EAGAIN)
        {
            DrainInto(trackIndex, decoder, frame);
            ret = decoder.SendPacket(pkt);
        }
        // TryReceiveFrame 側は失敗をログするのに SendPacket だけ無言だと、
        // 特定トラックが無音になったときに手がかりが残らない
        if (ret < 0)
            LogOnce(_sendPacketFailureLogged, trackIndex, $"SendPacket が失敗 track={trackIndex} ret={ret}");
        DrainInto(trackIndex, decoder, frame);
    }

    private void DrainInto(int trackIndex, AudioDecoder decoder, AVFrame* frame)
    {
        while (decoder.TryReceiveFrame(frame))
        {
            HandleDecodedFrame(trackIndex, decoder, frame);
            av_frame_unref(frame);
        }
    }

    private void HandleDecodedFrame(int trackIndex, AudioDecoder decoder, AVFrame* frame)
    {
        if (_prerollActive && TryHandlePreroll(trackIndex, decoder, frame))
            return;

        var pcm = decoder.ResampleFrame(frame);
        if (pcm == null)
        {
            LogOnce(_resampleFailureLogged, trackIndex, $"リサンプルに失敗（このトラックは無音になる） track={trackIndex}");
            HandleResampleFailure(trackIndex, decoder);
            return;
        }
        _resampleFailures.RecordSuccess(trackIndex);
        AddWithGate(trackIndex, pcm, 0, pcm.Length);
    }

    /// <summary>true を返した場合、通常のリサンプル＋追加はスキップ済み（preroll 側で処理を終えている）。</summary>
    private bool TryHandlePreroll(int trackIndex, AudioDecoder decoder, AVFrame* frame)
    {
        double offset = _getPtsSyncOffset();
        double framePts = double.IsNaN(offset) ? double.NaN : decoder.GetPtsSeconds(frame) - offset;
        if (double.IsNaN(framePts)) return false; // PTS 不明なら通常経路にフォールバック

        var action = PrerollCalculator.ComputeAction(
            framePts, decoder.NbSamples(frame), decoder.InSampleRate(frame),
            _prerollTarget, decoder.EffectiveOutSampleRate, AudioDecoder.OutChannels, sizeof(float));

        switch (action.Kind)
        {
            case PrerollActionKind.DropAll:
                return true; // resample すらせず破棄

            case PrerollActionKind.SkipBytes:
                var pcm = decoder.ResampleFrame(frame);
                if (pcm == null)
                {
                    // preroll 中の失敗を見逃すと、壊れたトラックが切り離されないまま通常経路へ進み、
                    // ミキサーの共通利用可能量を 0 に固定し続ける
                    LogOnce(_resampleFailureLogged, trackIndex, $"preroll 中のリサンプルに失敗 track={trackIndex}");
                    HandleResampleFailure(trackIndex, decoder);
                }
                else
                {
                    _resampleFailures.RecordSuccess(trackIndex);
                    int skip = Math.Min(action.SkipByteCount, pcm.Length);
                    if (skip < pcm.Length)
                        AddWithGate(trackIndex, pcm, skip, pcm.Length - skip);
                    else
                        // PrerollCalculator の見積もりと swr_convert の実出力長は一致する保証がないため、
                        // skip がフレーム長以上になると実質 DropAll になる。無言で消さず記録する
                        Diagnostics.DiagnosticLog.Write("audio",
                            $"preroll の skip 量がフレーム長以上のため全破棄 track={trackIndex} skip={action.SkipByteCount} len={pcm.Length}");
                }
                _prerollActive = false;
                return true;

            default: // KeepAll
                _prerollActive = false;
                return false; // 通常経路でそのまま追加させる
        }
    }

    // 通常運用でも充填ゲートの出入り自体は頻発するため、その都度はログしない。
    // ここで長く滞留するのは「ミキサーの Read そのものが呼ばれていない」ときだけで、
    // その原因は一時停止（正常）と音声出力の死亡（異常）の両方がありうる。この 2 つを
    // 経過時間では区別できないため、記録は WriteFatal へ上げず診断ログに留める。
    // なお HoldOutput 中は消費が続く（MultiTrackMixer.Read 参照）ので滞留の原因にはならない
    private static readonly TimeSpan GateStallLogThreshold = TimeSpan.FromSeconds(2.0);

    /// <summary>
    /// デコード済みサンプルをトラックのバッファへ足す。残量が閾値を超えている間は待つ（充填ゲート）。
    /// </summary>
    /// <remarks>
    /// シーク世代の照合は待機ループの中だけで行う。ゲートに掛からない経路（残量が閾値以下）では
    /// シーク前のサンプルが混ざりうるが、<c>MediaEngine.Seek</c> が「ミキサー出力の保留
    /// （<c>HoldOutput</c>）を立てる → バッファを空にする → <c>RequestSeek</c>」の順で進めるため、
    /// 混ざったサンプルは出力される前に保留され、直後に処理される Flush 番兵が同じバッファを空にする。
    /// **この順序が保証しているので、Seek 側で並べ替えるならここでも照合が必要になる**
    /// （毎フレーム世代を読む形になり、順序を保つ方が安い）。
    /// <c>_mixer</c> が無い（音声トラックなし）場合は出力自体が無いので問題にならない。
    /// </remarks>
    private void AddWithGate(int trackIndex, byte[] pcm, int offset, int count)
    {
        var track = _states[trackIndex];
        if (track.Buffer.BufferedDuration > FillGateThreshold)
        {
            long enterTicks = Environment.TickCount64;
            long lastLogElapsedMs = 0;
            while (track.Buffer.BufferedDuration > FillGateThreshold)
            {
                if (_stopRequested) return;
                // シークが入ったら、抱えているサンプルはシーク前の残骸。破棄して Get() へ戻り、
                // Flush 番兵を処理する。この脱出条件を持たないと、シーク時にゲートを抜けられる
                // 根拠が「MediaEngine.Seek が RequestSeek の手前で全トラックのバッファを空にする」
                // という別スレッドの副作用だけになる。実際に抜けられてはいるが、待機の脱出条件が
                // シークに言及していない形は ensemble-review.md §7（事実を代理値で置き換えるな）
                // が禁じているもので、あちらを変えた瞬間に恒久ブロックへ化ける
                SeekEpoch queueEpoch = _queue.Epoch;
                if (queueEpoch != HandledEpoch)
                {
                    Diagnostics.DiagnosticLog.Write("audio-gate",
                        $"充填ゲート中にシークを検出しサンプルを破棄 track={trackIndex} handled={HandledEpoch} current={queueEpoch}");
                    return;
                }
                _wake.Wait(FillGatePollInterval);
                _wake.Reset();

                long elapsedMs = Environment.TickCount64 - enterTicks;
                if (elapsedMs - lastLogElapsedMs >= GateStallLogThreshold.TotalMilliseconds)
                {
                    lastLogElapsedMs = elapsedMs;
                    Diagnostics.DiagnosticLog.Write("audio-gate",
                        $"充填ゲート滞留中 track={trackIndex} elapsedMs={elapsedMs} bufferedDuration={track.Buffer.BufferedDuration.TotalSeconds:F3}");
                }
            }
        }
        if (_anchorNotifyPending)
        {
            _anchorNotifyPending = false;
            // VideoDecodeThread と同じ理由の世代チェック: この間に次のシークが割り込んで
            // キューの世代が進んでいたら、この完了通知はもう無効な世代のもの。
            // 錨要求もプリロール完了通知も発火しない（本物の Flush が後で来て正しく上書きする）
            if (_queue.Epoch == _prerollEpoch)
                _onFirstSamplesAfterFlush?.Invoke(_anchorTarget);
            else
                Diagnostics.DiagnosticLog.Write("audio", $"stale preroll 破棄 prerollEpoch={_prerollEpoch} currentEpoch={_queue.Epoch} target={_anchorTarget:F3}");
        }
        // 切り離したトラックは IsEof を立てているため、ここでバッファへ足すとミキサーの除外条件
        //（EOF かつ残量ゼロ）から外れ、再び共通利用可能量の計算に巻き込まれる。中途半端に
        // 復帰させず、次のシーク（Flush）まで破棄し続ける
        if (_abandonedTracks[trackIndex]) return;
        track.Buffer.AddSamples(pcm, offset, count);
    }
}
