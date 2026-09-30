using System.Linq;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MultiTrackPlayer.Engine.Audio;

public class MultiTrackMixer : IWaveProvider
{
    private readonly List<AudioTrackState> _tracks = new();
    private readonly WaveFormat _format;
    private readonly int _blockAlign;
    /// <summary>マスター音量の既定値。MediaEngine 側の保持フィールドもこの値を初期値に使う。</summary>
    public const float DefaultMasterVolume = 1.0f;

    private float _masterVolume = DefaultMasterVolume;
    private byte[] _scratch = Array.Empty<byte>();

    public WaveFormat WaveFormat => _format;

    /// <summary>実際に混合された音声のフレーム数（Read 完了ごと）。PlaybackClock.OnAudioWritten に配線する。</summary>
    public Action<long>? OnAudioWritten;
    /// <summary>無音で埋めたフレーム数（アンダーラン/バッファ待ち等）。PlaybackClock.OnSilenceWritten に配線する。</summary>
    public Action<long>? OnSilenceWritten;
    /// <summary>Read() 完了ごとに呼ばれる。AudioDecodeThread の充填ゲート待ちを起こすためのフック。</summary>
    public Action? OnRead;

    /// <summary>
    /// 保留がこれより長く続いたら、読み進め（読んだ分は捨てる）を再開する。
    /// 実測の映像プリロールは最長 2.4 秒だったので、それを十分に超える値にしてある。
    /// </summary>
    /// <remarks>
    /// <c>MediaEngine.PrerollGraceMs</c>（滞留検出がプリロール待ちを見逃す猶予）と値は同じだが、別の事実を
    /// 表すので共有しない。あちらは「いつから異常と報告するか」、こちらは「いつ詰まりの逃げ道を開くか」。
    /// どちらかを変えても、もう片方を追従させる義務はない。
    /// <para>
    /// 保留中に読み進めなくても詰まらないのは、音声のパケットキューが充填ゲートの先にさらに数秒ぶん
    /// （トラック数 × 256 パケット。各トラックが均等に並んでいれば AAC で約 5.5 秒）を受け止められるから。demux はその分だけ先まで
    /// 読めるので、映像のプリロールが要るパケット（シーク先まで）は先に届いている。この猶予は、
    /// 音声がファイル内で映像より数秒以上先に置かれているような異常な配置のための保険。
    /// </para>
    /// </remarks>
    public static readonly TimeSpan HoldDiscardGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// true の間、トラックバッファに実データがあっても Read() は無音を返し、バッファも読み進めない。
    /// シーク直後、映像側のプリロール（キーフレーム→目標地点の破棄デコード）が完了するまで音声出力を
    /// 保留するために使う。これが無いと音声だけ先に実時間で進んでクロックが映像を置き去りにし、
    /// 映像が追いつこうとして大量ドロップ（早送りに見える）が発生する。
    /// <b>true を代入するたびに猶予の計時を始め直す</b>（シークのたびに <c>PrerollGate.BeginSeek</c> が立て直す）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>保留中に読み進めてはいけない。</b>以前は消費を続けていたため、保留が解けた時点で
    /// バッファの先頭が保留時間ぶん先へ進んでおり、クロックの錨（シーク先）より先の音声が鳴っていた。
    /// 次のシークまで音声が映像より先行したまま残る（実測で最大 2.4 秒）。
    /// </para>
    /// <para>
    /// 量ではなく時間で縛る。デコードは実時間よりずっと速く、映像のキューは数秒先まで先読みできるので、
    /// 「一定量を超えたら捨てる」形にすると保留の直後に上限へ届き、結局毎回捨てることになる（実機で確認）。
    /// </para>
    /// <para>
    /// 猶予を超えたら読み進めを再開するのは、保留が解けない異常（映像側がパケットを受け取れない等）で
    /// 充填ゲート→demux が止まったままになるのを防ぐため。読み進めた分は捨てることになり、解除後の音声が
    /// その分先行するので、再開したときは記録を残す。
    /// </para>
    /// </remarks>
    public bool HoldOutput
    {
        get => _holdOutput;
        set
        {
            if (value)
            {
                Volatile.Write(ref _holdStartedTicks, _tickSource());
                // 記録済みの印も保留を立てるたびに降ろす。Read 側で降ろすと、解除と次の保留の間に
                // Read が 1 回も挟まらなかった場合に持ち越され、次の猶予超過が記録されない
                _holdDiscardReported = false;
            }
            _holdOutput = value;
        }
    }

    private volatile bool _holdOutput;
    // 保留を立てた時刻（壁時計）。一時停止中は Read が呼ばれないが、この時刻からの経過は進み続ける。
    // 一時停止しても映像のプリロールは進んで保留は解けるので、猶予を使い切るのは保留が解けない異常のときだけ
    private long _holdStartedTicks;
    private readonly Func<long> _tickSource;

    private long _lastHoldOutputLogTicks;
    // 1 回の保留につき、猶予超過で捨て始めた記録は 1 行だけ残す（Read は 10ms 刻みで呼ばれる）
    private volatile bool _holdDiscardReported;

    public MultiTrackMixer() : this(() => Environment.TickCount64)
    {
    }

    /// <param name="tickSource">ミリ秒単位の単調な時刻（テストで猶予の経過を踏むために差し替える）。</param>
    internal MultiTrackMixer(Func<long> tickSource)
    {
        _tickSource = tickSource;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(
            Decoding.AudioDecoder.OutSampleRate,
            Decoding.AudioDecoder.OutChannels);
        _blockAlign = _format.BlockAlign;
    }

    public void AddTrack(AudioTrackState track) => _tracks.Add(track);
    public void RemoveAllTracks() => _tracks.Clear();
    public void SetMasterVolume(float v) => _masterVolume = Math.Clamp(v, 0f, 1f);

    public int Read(byte[] buffer, int offset, int count)
    {
        var outFloats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        outFloats.Clear();

        bool holding = HoldOutput;
        if (holding)
        {
            DiscardIfHoldOutlastedGrace(count);
            OnSilenceWritten?.Invoke(count / _blockAlign);
            OnRead?.Invoke();
            return count;
        }

        int common = ComputeCommonAvailableBytes(count);
        if (common > 0)
            MixCommonBytes(common, outFloats);

        long audioFrames = common / _blockAlign;
        long silenceFrames = (count - common) / _blockAlign;

        // 二度と実データが来ない無音は、アンダーラン/priming待ちの無音とは違う。ここで
        // OnSilenceWritten のままクロックを凍結させ続けると、音声より僅かに長い映像側の
        // 末尾フレームが永遠に「期限到来」と判定されず、再生完了検出（IsEofDrained）が
        // 働かなくなる（次の動画へ進めないバグの原因）。実時間で進め続けるべき区間なので
        // OnAudioWritten 側に計上する。
        //
        // 該当するのは次の 2 つ。All は空集合に対して true を返すため、同じ条件式で両方を捕まえられる:
        // ・全トラックが EOF に達した後の末尾無音
        // ・音声トラックを 1 本も持たない動画（無音動画）。こちらは OnAudioWritten が一度も
        //   発火しないとクロックが 0.0 に固定され、2 枚目以降の映像フレームが永久に
        //   「期限到来」と判定されないため、最初のフレームで再生が固まる（GPU/CPU 両経路）
        if (silenceFrames > 0 && _tracks.All(t => t.IsEof))
        {
            audioFrames += silenceFrames;
            silenceFrames = 0;
        }

        if (audioFrames > 0) OnAudioWritten?.Invoke(audioFrames);
        if (silenceFrames > 0) OnSilenceWritten?.Invoke(silenceFrames);

        OnRead?.Invoke();
        return count;
    }

    /// <summary>
    /// 保留中の Read。猶予（<see cref="HoldDiscardGrace"/>）内はバッファに手を付けない。猶予を超えたら
    /// 通常の再生と同じ歩調（全トラック共通量、最大 <paramref name="count"/> バイト）で読み進めて捨てる
    /// （理由は <see cref="HoldOutput"/> の remarks）。
    /// </summary>
    private void DiscardIfHoldOutlastedGrace(int count)
    {
        long heldMs = _tickSource() - Volatile.Read(ref _holdStartedTicks);
        if (heldMs < (long)HoldDiscardGrace.TotalMilliseconds)
        {
            LogHoldOutputStall(0);
            return;
        }

        int discard = ComputeCommonAvailableBytes(count);
        if (discard > 0)
        {
            EnsureScratchCapacity(discard);
            foreach (var track in _tracks)
                track.Buffer.Read(_scratch, 0, discard);

            if (!_holdDiscardReported)
            {
                _holdDiscardReported = true;
                // 常に残る側へ。捨てた分だけ解除後の音声が映像より先行するので、症状と結び付けられる
                // ようにしておく。Read は音声出力スレッドなので、ファイル I/O を伴わない遅延書き込みを使う
                Diagnostics.DiagnosticLog.WriteFatalDeferred("mixer",
                    $"シーク後の出力保留が {HoldDiscardGrace.TotalSeconds:F0} 秒を超えても解けないため、音声の読み進めを再開した"
                    + "（映像の準備が終わっていない可能性がある。読み進めた分は捨てるので、保留が解けた後の音声が映像より先行する）");
            }
        }

        LogHoldOutputStall(discard);
    }

    /// <summary>回帰検知用診断ログ: HoldOutput が長時間解除されない異常ケースを検出するため、
    /// HoldOutput 中に捨てた量とトラックのバッファ残量を一定間隔で記録する。</summary>
    private void LogHoldOutputStall(int discardedBytes)
    {
        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastHoldOutputLogTicks < 2000) return;
        _lastHoldOutputLogTicks = nowTicks;

        string bufferedByTrack = string.Join(",", _tracks.Select(t => t.Buffer.BufferedBytes));
        Diagnostics.DiagnosticLog.Write("mixer-hold",
            $"HoldOutput 中 出力保留 discardedBytes={discardedBytes} trackBufferedBytes=[{bufferedByTrack}]");
    }

    /// <summary>
    /// 全トラック共通の読める量だけをミックスする。トラックごとに独立して読むと、
    /// あるトラックだけアンダーラン/discard したときにトラック間の位相がずれるため、
    /// 常に全トラックから同量を消費してから合成する（ミュートトラックも消費だけは行う）。
    /// </summary>
    private int ComputeCommonAvailableBytes(int count)
    {
        if (_tracks.Count == 0) return 0;

        // EOF かつ残量も尽きたトラックだけを下限計算から除外する（そうしないと、
        // 完全に消費し終えたEOFトラックの残量ゼロが常に common=0 を強制し、他トラックの
        // 音声まで止めてしまう）。EOF でもまだ残量があるトラックは、その末尾を実時間で
        // ドレインし切る必要があるため通常どおり min 計算に含める。ここで除外してしまうと
        // 全トラックが同時に EOF になった瞬間 common が強制的に 0 になり、MixCommonBytes が
        // 呼ばれなくなって各トラックの末尾未消費データが二度と読み出されず、
        // BufferedBytes が 0 に落ちきらないまま再生完了検出（CheckPlaybackEnded）が
        // 永久に成立しなくなる不具合があった
        int common = int.MaxValue;
        foreach (var track in _tracks)
        {
            if (track.IsEof && track.Buffer.BufferedBytes == 0) continue;
            common = Math.Min(common, track.Buffer.BufferedBytes);
        }
        if (common == int.MaxValue) common = 0; // 全トラック EOF かつ残量ゼロ

        common = Math.Min(common, count);
        common -= common % _blockAlign;
        return Math.Max(0, common);
    }

    /// <summary>全トラックから <paramref name="common"/> バイトずつ読み、合成して出力へ書く。</summary>
    private void MixCommonBytes(int common, Span<float> outFloats)
    {
        EnsureScratchCapacity(common);
        var scratchBytes = _scratch;

        foreach (var track in _tracks)
        {
            int read = track.Buffer.Read(scratchBytes, 0, common);
            if (track.IsMuted) continue;

            float vol = track.Volume * _masterVolume;
            if (vol == 0f) continue;

            var srcFloats = MemoryMarshal.Cast<byte, float>(scratchBytes.AsSpan(0, read));
            for (int i = 0; i < srcFloats.Length; i++)
                outFloats[i] += srcFloats[i] * vol;
        }

        int floatCount = common / sizeof(float);
        for (int i = 0; i < floatCount; i++)
            outFloats[i] = Math.Clamp(outFloats[i], -1f, 1f);
    }

    private void EnsureScratchCapacity(int size)
    {
        if (_scratch.Length < size)
            _scratch = new byte[size];
    }
}
