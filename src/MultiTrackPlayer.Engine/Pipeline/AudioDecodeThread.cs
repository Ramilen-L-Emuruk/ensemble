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
    private readonly ManualResetEventSlim _wake = new(false);

    private volatile bool _stopRequested;
    private readonly object _seekTargetLock = new();
    // VideoDecodeThread と同じ理由（コメント参照）で FIFO ではなく Flush 番兵の Serial キー付き辞書にする
    private readonly Dictionary<int, double> _pendingSeekTargets = new();
    private bool _prerollActive;
    private double _prerollTarget;
    // このプリロールが属するキュー世代（Flush 番兵自身の Serial）。VideoDecodeThread と同じ理由で、
    // 次のシークに割り込まれた後の「無効な世代のプリロール完了」による誤アンカーを防ぐために使う
    private int _prerollSerial;
    private bool _anchorNotifyPending;
    private double _anchorTarget;

    // 異常が続くと毎フレーム記録されてログが埋まるため、トラックごとに最初の 1 回だけ残す
    /// <summary>最後に処理した Flush 番兵の世代。キューの Serial と一致していればシークに追いついている。</summary>
    public int HandledSerial => _handledSerial;
    private volatile int _handledSerial;

    private readonly bool[] _resampleFailureLogged;
    private readonly bool[] _sendPacketFailureLogged;

    /// <exception cref="ArgumentException">decoders と states の件数が一致しない場合。</exception>
    public AudioDecodeThread(
        IReadOnlyList<AudioDecoder> decoders, IReadOnlyList<AudioTrackState> states,
        AudioPacketQueue queue, Func<double> getPtsSyncOffset,
        Action<double>? onFirstSamplesAfterFlush = null)
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
        _resampleFailureLogged = new bool[decoders.Count];
        _sendPacketFailureLogged = new bool[decoders.Count];
    }

    /// <summary>
    /// DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと。
    /// serial は、これから投入される Flush 番兵自身の Serial（呼び出し側で Flush() 前のキュー Serial + 1 として算出）。
    /// </summary>
    public void SetSeekTarget(int serial, double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _pendingSeekTargets[serial] = normalizedTargetSeconds;
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
                        HandleFlush(item.Serial);
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

    private void HandleFlush(int serial)
    {
        // キューの Serial と突き合わせて「このスレッドがシークに追いついたか」を外から判断できるようにする
        _handledSerial = serial;
        for (int i = 0; i < _decoders.Count; i++)
        {
            _decoders[i].FlushBuffers();
            _states[i].Buffer.ClearBuffer();
            _states[i].IsEof = false;
        }
        _prerollSerial = serial;
        lock (_seekTargetLock)
        {
            _prerollActive = _pendingSeekTargets.Remove(serial, out _prerollTarget);
            if (!_prerollActive) _prerollTarget = double.NaN;
            // この番兵より前の serial 宛ての目標は、対応する番兵が Flush() の Clear() で
            // 消えて二度と来ない残骸。溜め続けると意味のない対応関係が残るので掃除する
            if (_pendingSeekTargets.Count > 0)
            {
                foreach (int staleKey in _pendingSeekTargets.Keys.Where(k => k <= serial).ToList())
                    _pendingSeekTargets.Remove(staleKey);
            }
        }
        // クロックの錨（anchor）はシーク後最初の「新しい」音声サンプル投入時に要求する。
        // UI の Seek() 時点で要求すると、ミキサーに残る旧位置の音声で錨が早期消費されて
        // クロックとA/Vが恒久的にズレる（実機検証で -56s のズレとして観測されたバグ）。
        _anchorNotifyPending = _prerollActive;
        _anchorTarget = _prerollTarget;
        Diagnostics.DiagnosticLog.Write("audio", $"flush 処理 serial={serial} preroll={(_prerollActive ? _prerollTarget.ToString("F3") : "なし")}");
    }

    private void HandleEof(AVFrame* frame)
    {
        // プリロール中のまま終端に達すると完了通知が出ず、ミキサーの出力保留が解けない
        if (_prerollActive)
        {
            _prerollActive = false;
            _anchorNotifyPending = false;
            if (_queue.Serial == _prerollSerial)
            {
                Diagnostics.DiagnosticLog.Write("audio", $"EOF 到達のためプリロールを完了扱いにする target={_prerollTarget:F3}");
                _onFirstSamplesAfterFlush?.Invoke(_prerollTarget);
            }
        }
        for (int i = 0; i < _decoders.Count; i++)
        {
            _decoders[i].SendPacket(null);
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
        if (pcm != null) AddWithGate(trackIndex, pcm, 0, pcm.Length);
        else LogOnce(_resampleFailureLogged, trackIndex, $"リサンプルに失敗（このトラックは無音になる） track={trackIndex}");
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
                if (pcm != null)
                {
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
    // ここでの滞留はミキサー側の消費停止（HoldOutput 等）を示す異常シグナルなので、
    // 一定時間を超えて抜けられない場合だけ記録する
    private static readonly TimeSpan GateStallLogThreshold = TimeSpan.FromSeconds(2.0);

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
            // キューの Serial が進んでいたら、この完了通知はもう無効な世代のもの。
            // 錨要求もプリロール完了通知も発火しない（本物の Flush が後で来て正しく上書きする）
            if (_queue.Serial == _prerollSerial)
                _onFirstSamplesAfterFlush?.Invoke(_anchorTarget);
            else
                Diagnostics.DiagnosticLog.Write("audio", $"stale preroll 破棄 prerollSerial={_prerollSerial} currentSerial={_queue.Serial} target={_anchorTarget:F3}");
        }
        track.Buffer.AddSamples(pcm, offset, count);
    }
}
