using MultiTrackPlayer.Engine.Audio;
using MultiTrackPlayer.Engine.Decoding;
using Sdcb.FFmpeg.Raw;
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
    private double _nextSeekTarget = double.NaN;
    private bool _prerollActive;
    private double _prerollTarget;
    // このプリロールが属するキュー世代（Flush 番兵自身の Serial）。VideoDecodeThread と同じ理由で、
    // 次のシークに割り込まれた後の「無効な世代のプリロール完了」による誤アンカーを防ぐために使う
    private int _prerollSerial;
    private bool _anchorNotifyPending;
    private double _anchorTarget;

    public AudioDecodeThread(
        IReadOnlyList<AudioDecoder> decoders, IReadOnlyList<AudioTrackState> states,
        AudioPacketQueue queue, Func<double> getPtsSyncOffset,
        Action<double>? onFirstSamplesAfterFlush = null)
    {
        _decoders = decoders;
        _states = states;
        _queue = queue;
        _getPtsSyncOffset = getPtsSyncOffset;
        _onFirstSamplesAfterFlush = onFirstSamplesAfterFlush;
    }

    /// <summary>DemuxThread のシーク処理から、Flush 番兵を投入する前に呼ぶこと。</summary>
    public void SetSeekTarget(double normalizedTargetSeconds)
    {
        lock (_seekTargetLock) _nextSeekTarget = normalizedTargetSeconds;
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
        finally
        {
            av_frame_free(&frame);
        }
    }

    private void HandleFlush(int serial)
    {
        for (int i = 0; i < _decoders.Count; i++)
        {
            _decoders[i].FlushBuffers();
            _states[i].Buffer.ClearBuffer();
            _states[i].IsEof = false;
        }
        _prerollSerial = serial;
        lock (_seekTargetLock)
        {
            _prerollActive = !double.IsNaN(_nextSeekTarget);
            _prerollTarget = _nextSeekTarget;
            _nextSeekTarget = double.NaN;
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
        for (int i = 0; i < _decoders.Count; i++)
        {
            _decoders[i].SendPacket(null);
            while (_decoders[i].TryReceiveFrame(frame))
            {
                var pcm = _decoders[i].ResampleFrame(frame);
                if (pcm != null) AddWithGate(i, pcm, 0, pcm.Length);
                av_frame_unref(frame);
            }
            _states[i].IsEof = true;
        }
    }

    private void HandlePacket(int trackIndex, AVPacket* pkt, AVFrame* frame)
    {
        if (trackIndex < 0 || trackIndex >= _decoders.Count) return;
        var decoder = _decoders[trackIndex];

        int ret = decoder.SendPacket(pkt);
        while (ret == -EAGAIN)
        {
            DrainInto(trackIndex, decoder, frame);
            ret = decoder.SendPacket(pkt);
        }
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
                }
                _prerollActive = false;
                return true;

            default: // KeepAll
                _prerollActive = false;
                return false; // 通常経路でそのまま追加させる
        }
    }

    private void AddWithGate(int trackIndex, byte[] pcm, int offset, int count)
    {
        var track = _states[trackIndex];
        while (track.Buffer.BufferedDuration > FillGateThreshold)
        {
            if (_stopRequested) return;
            _wake.Wait(FillGatePollInterval);
            _wake.Reset();
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
