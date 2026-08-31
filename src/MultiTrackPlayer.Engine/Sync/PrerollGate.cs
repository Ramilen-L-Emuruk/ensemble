namespace MultiTrackPlayer.Engine.Sync;

/// <summary>プリロール完了通知を受け取った結果。</summary>
public enum PrerollNotifyResult
{
    /// <summary>待っている世代のものではないため捨てた。</summary>
    Stale,

    /// <summary>受け付けたが、もう片方のプリロールがまだ完了していない。</summary>
    Pending,

    /// <summary>受け付けて音声・映像の両方がそろった（この時点で保留は解除済み）。</summary>
    Satisfied,
}

/// <summary>
/// シーク後、音声・映像の<b>両方</b>のプリロールが終わるまでミキサーの実音声出力を保留するための門番。
///
/// <para>
/// 保留が要る理由: 映像のプリロール（キーフレーム→目標地点までの破棄デコード）は実時間がかかる。
/// 音声だけ先に終えて実時間で流し始めるとクロックが映像を置き去りにし、映像が追いつこうとして
/// 大量にドロップする（早送りに見える）。
/// </para>
///
/// <para>
/// <b>この型が世代を持つ理由。</b> 完了通知を出すデコードスレッドは
/// 「自分のプリロール世代 == パケットキューの現在世代」を確かめてから発火する。しかし
/// キューの世代が進むのは demux スレッドが実際にシークして <c>Flush</c> した時点であって、
/// <see cref="MediaEngine.Seek"/> が <c>RequestSeek</c> を呼んだ時点ではない。その間
/// （<c>avformat_seek_file</c> を含むので数十 ms 開きうる）、前のシークの完了通知は
/// デコード側の照合を素通りする。受け取る側でも世代を等値照合しないとこの窓は塞げない。
/// </para>
///
/// <para>
/// <b>待つ世代を確定する順序が肝。</b> <see cref="BeginSeek"/> の時点では、そのシークの世代は
/// まだ採番されていない（採番するのは <see cref="Pipeline.DemuxThread.RequestSeek"/> だけ。
/// <see cref="SeekEpoch"/> 参照）。そこで一旦「待つ世代なし」にして古い通知を全て落とし、
/// 採番と同時に <see cref="IssueEpoch"/> で確定させる。
/// <b><see cref="IssueEpoch"/> は採番のクリティカルセクション内から呼ぶこと。</b>
/// 外に出すと、その世代の完了通知が確定より先に届いて <see cref="PrerollNotifyResult.Stale"/> として
/// 捨てられ、保留が永久に解けない（<c>.claude/rules/ensemble-review.md</c> §1 の恒久ブロック）。
/// </para>
///
/// <para>
/// <b>保留の反映もこの型が行う。</b> 判定（ロック内）と実際の <c>HoldOutput</c> への書き込みを
/// 分けると、その隙間に次のシークが立てた保留を旧シークの解除が上書きしうる。世代照合が正しく
/// 働いていても起きる別種の競合なので、<c>applyHold</c> はすべてロック内から呼ぶ。
/// </para>
///
/// <para>
/// スレッド安全。UI スレッド（<see cref="BeginSeek"/>）・demux スレッド（<see cref="IssueEpoch"/>）・
/// 映像／音声のデコードスレッド（各 <c>Notify</c>）から呼ばれる。
/// ロック順序は <c>DemuxThread._seekLock</c> → このクラスのロックの一方向のみ
/// （<see cref="IssueEpoch"/> がそこから呼ばれるため）。逆順に取る経路を作らないこと。
/// </para>
/// </summary>
public sealed class PrerollGate
{
    private readonly object _lock = new();

    /// <summary>
    /// 保留の要否を反映する。<b>このロック内から呼ばれる</b>ため、
    /// 渡す実装は別のロックを取らず、ブロックしうる処理（ファイル I/O・プロセス間ミューテックス）も
    /// 行わないこと。
    /// </summary>
    private readonly Action<bool> _applyHold;

    /// <summary>待っているシーク世代。<c>null</c> は「このシークの世代がまだ採番されていない」。</summary>
    private SeekEpoch? _awaited;

    private bool _videoReady = true;
    private bool _audioReady = true;

    /// <param name="applyHold">
    /// ミキサーの実音声出力を保留するか否かを反映する処理。<c>true</c> で保留、<c>false</c> で解除。
    /// </param>
    public PrerollGate(Action<bool> applyHold)
    {
        _applyHold = applyHold;
    }

    /// <summary>待っているシーク世代。記録用（判定には使わないこと）。</summary>
    public SeekEpoch? AwaitedEpoch { get { lock (_lock) return _awaited; } }

    /// <summary>
    /// シーク後のプリロールをまだ待っている（＝どちらかの側が着地後の最初のデータを出せていない）か。
    /// </summary>
    /// <remarks>
    /// 「映像が止まっている」ことが異常かどうかの判断に使う（<c>MediaEngine.DetectVideoStall</c>）。
    /// シークの着地フレームが出るまで提示は止まるが、それは正常な待ちであって異常ではない。
    /// 保留の有無そのものを見るので、<see cref="BeginSeek"/> が「待つ相手がいない」と判断した場合
    /// （映像も音声も無い）は false になる。
    /// </remarks>
    public bool IsWaitingForPreroll { get { lock (_lock) return !(_videoReady && _audioReady); } }

    /// <summary>
    /// 待つものが何も無い状態へ戻す。停止・パイプライン再構築時に呼ぶ。
    /// シーク中断のまま停止した場合に、保留状態を次の再生へ持ち越さないため。
    /// <para>
    /// これ以降 <see cref="BeginSeek"/> までは、どの世代の通知も
    /// <see cref="PrerollNotifyResult.Stale"/> になり保留へ触れない。
    /// </para>
    /// </summary>
    /// <param name="hold">
    /// 戻した後の保留の状態。通常の停止は <c>false</c>（解除）。検疫のように「待ち合わせとしては
    /// 何も待たないが、消音のため保留は立てたままにする」場合は <c>true</c>。
    /// <b>呼び出し側が解除と設定を二段で書かないためのもの。</b> 二段で書くと、その隙間に
    /// ミキサーの <c>Read</c> が走って意図しない音が一瞬だけ実出力へ漏れる。
    /// </param>
    public void Reset(bool hold)
    {
        lock (_lock)
        {
            _awaited = null;
            _videoReady = true;
            _audioReady = true;
            _applyHold(hold);
        }
    }

    /// <summary>
    /// シークの開始を記録し、待つ側があれば保留を立てる。
    /// <b>世代が採番される前</b>に呼ぶこと（前の世代の完了通知を落とすため）。
    /// 存在しない側（映像なしファイルの映像等）は待たない。
    /// </summary>
    /// <returns>保留を立てたか。<c>false</c> は待つ相手がいない（映像も音声も無い）場合。</returns>
    public bool BeginSeek(bool hasVideo, bool hasAudio)
    {
        lock (_lock)
        {
            _awaited = null;
            _videoReady = !hasVideo;
            _audioReady = !hasAudio;
            bool hold = !(_videoReady && _audioReady);
            _applyHold(hold);
            return hold;
        }
    }

    /// <summary>
    /// このシークの世代を確定する。<b>採番のクリティカルセクション内から呼ぶこと</b>（型の説明を参照）。
    /// </summary>
    public void IssueEpoch(SeekEpoch epoch)
    {
        lock (_lock) _awaited = epoch;
    }

    /// <summary>映像プリロールの完了通知。そろった時点で保留の解除まで行う。</summary>
    public PrerollNotifyResult NotifyVideoReady(SeekEpoch epoch)
    {
        lock (_lock)
        {
            if (_awaited != epoch) return PrerollNotifyResult.Stale;
            _videoReady = true;
            return Settle();
        }
    }

    /// <summary>音声プリロールの完了通知。そろった時点で保留の解除まで行う。</summary>
    public PrerollNotifyResult NotifyAudioReady(SeekEpoch epoch)
    {
        lock (_lock)
        {
            if (_awaited != epoch) return PrerollNotifyResult.Stale;
            _audioReady = true;
            return Settle();
        }
    }

    /// <summary>ロックを保持したまま呼ぶこと。</summary>
    private PrerollNotifyResult Settle()
    {
        if (!(_videoReady && _audioReady)) return PrerollNotifyResult.Pending;
        _applyHold(false);
        return PrerollNotifyResult.Satisfied;
    }
}
