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

    /// <summary>
    /// 「デコーダが前進しない」を何回連続で観測したらトラックを畳むか。
    /// 値は <see cref="ResampleFailureTracker.DefaultThreshold"/> と同じ 50 にしてあるが、
    /// 数えている事実が違う（あちらはリサンプル失敗）ので互いに追従する義務はない。
    /// <para>
    /// 数えている単位はデコード後のフレームではなく<b>受け取ったパケット</b>。1 パケットの時間長は
    /// コーデック依存（AAC のような固定フレーム長なら 1024 サンプル＝48kHz で約 21ms だが、
    /// Opus の可変フレーム長や生 PCM では大きく変わる）。したがって 50 回が何秒ぶんに当たるかは
    /// ファイルによる。おおむね数百 ms〜1 秒程度を見込んだ値で、厳密な時間の保証ではない。
    /// </para>
    /// <para>
    /// ResampleFailureTracker を共用せず別に数えているのは、畳んだ理由を取り違えないため。
    /// 共用すると「リサンプル 30 回＋前進不能 20 回」で閾値に達し、記録に残す理由が実態と食い違う。
    /// </para>
    /// </summary>
    private const int NoProgressAbandonThreshold = 50;
    private static readonly TimeSpan FillGatePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IReadOnlyList<AudioDecoder> _decoders;
    private readonly IReadOnlyList<AudioTrackState> _states;
    private readonly AudioPacketQueue _queue;
    private readonly Func<double> _getPtsSyncOffset;
    private readonly Action<SeekEpoch, double>? _onFirstSamplesAfterFlush;
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
    // EOF ドレインの flush パケット失敗。通常デコード中の失敗（NoteNotProgressing が数える）とは
    // 原因もタイミングも別なので、抑制を分けて片方が他方を隠さないようにする
    private readonly bool[] _eofFlushFailureLogged;
    // 規約違反（WriteFatal で常に残す）の抑制。診断ログ限りの失敗と分けているのは、
    // 先に起きた軽い失敗が規約違反の記録を永久に打ち消さないようにするため
    private readonly bool[] _dataAfterDrainingLogged;
    private readonly bool[] _eofFlushDoubleSendLogged;
    // 「このパケットは送れなかった」を連続で観測した回数（NoteNotProgressing が数える）。
    // 記録の抑制もこの値で行うため、専用フラグは置かない
    private readonly int[] _noProgressStreak;

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
        Action<SeekEpoch, double>? onFirstSamplesAfterFlush = null,
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
        _eofFlushFailureLogged = new bool[decoders.Count];
        _dataAfterDrainingLogged = new bool[decoders.Count];
        _eofFlushDoubleSendLogged = new bool[decoders.Count];
        _noProgressStreak = new int[decoders.Count];
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

    /// <summary>呼び出し規約違反のように、診断ログが無効でも残さなければ追えない事象を 1 回だけ記録する。</summary>
    private static void LogFatalOnce(bool[] logged, int trackIndex, string message)
    {
        if (trackIndex < 0 || trackIndex >= logged.Length || logged[trackIndex]) return;
        logged[trackIndex] = true;
        Diagnostics.DiagnosticLog.WriteFatal("audio", message);
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
            _noProgressStreak[i] = 0;
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
            // AVERROR_EOF は「既に draining 状態のデコーダへ flush パケットを送った」の意。
            // EOF 番兵は DemuxThread が _eofReached で 1 回に絞り、次の EOF までに必ず
            // Flush 番兵（HandleFlush の FlushBuffers）が挟まるため、ここは常に 1 回目のはず。
            // 返ってきたら呼び出し規約違反なので、診断ログの有効・無効に関わらず残す
            if (flushRet == AVERROR_EOF)
                LogFatalOnce(_eofFlushDoubleSendLogged, i,
                    $"EOF ドレインの flush パケットが二重送信された track={i}（終端付近の音が欠ける）");
            else if (flushRet < 0)
                LogOnce(_eofFlushFailureLogged, i,
                    $"EOF ドレインの SendPacket が失敗 track={i} ret={flushRet}");
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
        _onFirstSamplesAfterFlush?.Invoke(_prerollEpoch, target);
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
            // 1 枚も吸えていないのに再送してもデコーダの状態は変わらないため、前進を確かめずに
            // 回すとこのループは永久に抜けない（VideoDecodeThread.HandlePacket と同じ形。
            // あちらは停止要求でドレインが止まるぶん確実に踏むが、こちらも TryReceiveFrame が
            // エラーを返し続ければ同じことになる）。
            //
            // 対称性を求めて DrainInto へ停止要求のガードを足してはいけない。映像側でこのループが
            // 確実にスピンするのは、まさにそのガードで停止要求中に 1 枚も吸わなくなるからで、
            // 足せば同じ危険を音声側にも作ることになる。停止は Run のループ条件で見れば足りる
            if (!DrainInto(trackIndex, decoder, frame))
            {
                // 記録だけで抜けてはいけない。ミキサーは「EOF かつ残量ゼロ」のトラックしか
                // 共通利用可能量の計算から除外しないため、残量ゼロのまま居座らせると common が
                // 0 に固定され、健全な他トラックまで無音になる（AbandonTrack の doc コメント参照）。
                // しかもその無音は滞留検出に引っかからない（StallDetector が見る Read は呼ばれ続ける）。
                //
                // ただし単発では畳まない。TryReceiveFrame は -EAGAIN・AVERROR_EOF・本物のデコード
                // エラーをまとめて false にするため、破損パケット由来の単発エラーもここへ来る。
                // 一度で畳むと、次のパケットで回復できるトラックを残り再生時間ずっと無音にして
                // しまう（ResampleFailureTracker が閾値方式を採っているのと同じ理由）
                NoteNotProgressing(trackIndex, "デコーダが入力を受け付けず出力も出せない");
                return;
            }
            ret = decoder.SendPacket(pkt);
        }
        // AVERROR_EOF は「draining 状態のデコーダへ通常パケットを送った」の意。EOF 番兵の後に
        // Flush 番兵を挟まず Data パケットが来たことになり、HandleEof の二重送信と同じ規約違反。
        // 連続回数には数えない。この状態なら HandleEof が既に走っていて IsEof が立っており、
        // ミキサーの共通利用可能量からは除外済みなので、畳む必要が無い
        if (ret == AVERROR_EOF)
        {
            LogFatalOnce(_dataAfterDrainingLogged, trackIndex,
                $"draining 状態のデコーダへ通常パケットを送った track={trackIndex}（Flush 番兵を挟まず Data が届いた）");
        }
        else if (ret < 0)
        {
            // 送信が恒常的に失敗するトラックは、-EAGAIN で詰まるのと結末が同じ。フレームが
            // 出ないまま IsEof も立たず、ミキサーの共通利用可能量を 0 に固定して健全な他トラックまで
            // 無音にする。症状も畳む判断も同じなので同じカウンタで数える。TryReceiveFrame 側は
            // 失敗をログするのに SendPacket だけ無言だと、特定トラックが無音になったときに
            // 手がかりが残らないため、記録も NoteNotProgressing に任せる（初回の 1 行に ret を載せている）
            NoteNotProgressing(trackIndex, $"SendPacket が失敗した（ret={ret}）");
        }
        else
        {
            // パケットが受け付けられた＝このトラックは前進した。連続失敗を数え直す。
            // ここで戻さないと、ファイル全体に散らばった単発の失敗が積み上がって「連続」ではない
            // 数え方になり、健全なトラックがいつか閾値に達して畳まれる
            _noProgressStreak[trackIndex] = 0;
        }
        DrainInto(trackIndex, decoder, frame);
    }

    /// <summary>
    /// このパケットをデコーダへ送れなかったことを記録し、連続が閾値に達したらトラックを畳む。
    /// <para>
    /// 畳むのが要る理由は <see cref="AbandonTrack"/> の doc を参照（残量ゼロのトラックを
    /// 居座らせるとミキサーの共通利用可能量が 0 に固定され、健全な他トラックまで無音になる）。
    /// 単発で畳まないのは、破損パケット由来の一時的な失敗で回復できるトラックを
    /// 残り再生時間ずっと無音にしてしまわないため（<see cref="ResampleFailureTracker"/> と同じ判断）。
    /// </para>
    /// <para>
    /// 記録は初回だけ診断ログへ。閾値に達しないまま回復したケースは <see cref="AbandonTrack"/> の
    /// 記録に現れないため、「音が途切れたが回復した」を追う手がかりをここで残す。
    /// 常に残す <c>WriteFatal</c> は、畳むと確定した時点（<see cref="AbandonTrack"/>）に任せる。
    /// </para>
    /// <para>
    /// 対象は<b>送信できなかった</b>場合だけで、「送信は通ったのに
    /// <c>avcodec_receive_frame</c> が本物のエラーを返し続ける」経路は数えていない
    /// （<c>TryReceiveFrame</c> がエラーと -EAGAIN を <c>false</c> に畳んでいるため区別できない）。
    /// </para>
    /// </summary>
    /// <param name="trackIndex">対象トラック。</param>
    /// <param name="reason">ログに残す理由。</param>
    private void NoteNotProgressing(int trackIndex, string reason)
    {
        // 閾値に達した瞬間だけ畳む。ResampleFailureTracker.RecordFailure が「その瞬間だけ true」を
        // 返すのと同じ意味づけで、超えた後も呼び続けても AbandonTrack を無駄に再実行しない
        // 理由は括弧に入れて渡す。テンプレートの地の文へ直に埋めると reason の文末
        //（体言か述語か）に文法が依存し、呼び出し側を増やすたびに日本語が壊れる
        int streak = ++_noProgressStreak[trackIndex];
        if (streak == 1)
            Diagnostics.DiagnosticLog.Write("audio",
                $"パケットを破棄 track={trackIndex}（{reason}）。"
                + $"連続 {NoProgressAbandonThreshold} 回でこのトラックを畳む");
        else if (streak == NoProgressAbandonThreshold)
            AbandonTrack(trackIndex,
                $"前進しない状態が {NoProgressAbandonThreshold} 回連続した（{reason}）");
    }

    /// <summary>デコーダの出力を吸い出してトラックのバッファへ流す。</summary>
    /// <returns>1 枚以上取り出した場合 true。デコードエラーで 1 枚も取り出せなかった場合 false
    /// （呼び出し側は「再送しても前進しない」と判断する）。</returns>
    private bool DrainInto(int trackIndex, AudioDecoder decoder, AVFrame* frame)
    {
        bool drainedAny = false;
        while (decoder.TryReceiveFrame(frame))
        {
            drainedAny = true;
            HandleDecodedFrame(trackIndex, decoder, frame);
            av_frame_unref(frame);
        }
        return drainedAny;
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
    // その原因は一時停止（正常）と音声出力の死亡（異常）の両方がありうる。**この 2 つを
    // 区別できないのは、ここが再生状態を知らないからで、経過時間の測り方の問題ではない。**
    // 生死の判定は再生状態を持つ MediaEngine.DetectAudioStall（StallDetector）が担い、
    // ここでの滞留ログはその裏付けに使う診断情報にとどめる（WriteFatal へは上げない）。
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
                _onFirstSamplesAfterFlush?.Invoke(_prerollEpoch, _anchorTarget);
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
