using System.Collections.Concurrent;
using MultiTrackPlayer.Engine.Audio;
using NAudio.Wave;

namespace MultiTrackPlayer.Tests.Integration;

/// <summary>
/// 実デバイスを持たない音声出力。<b>テストが時間を握る</b>ための仕掛けで、
/// <see cref="AdvanceMs"/> を呼んだ分だけミキサーから読み出し、その分だけ再生位置が進む。
/// </summary>
/// <remarks>
/// <b>なぜ必要か</b>: このプロジェクトの不具合は「音声出力が回っている間の並行処理」に集中して
/// いるのに、本番の <c>WasapiOut</c> は実デバイスを要求し、しかも読み出しの間隔をこちらから
/// 決められない。時間を握れれば、滞留検出の閾値もプリロールの解除も<b>決定的に</b>踏める。
/// <para>
/// <b>読み出しは専用スレッドで行う。</b> 本番では WASAPI のレンダースレッドがミキサーの
/// <c>Read</c> を呼ぶため、テストのスレッドから直接呼ぶと<b>スレッドの組み合わせが本番と
/// 変わる</b>（<c>Read</c> の中で動くフック——<c>OnAudioWritten</c> からクロックへの反映——が
/// テストスレッドで走ることになり、拾いたい競合が起きなくなる）。
/// <see cref="AdvanceMs"/> は読み出しが終わるまで待つので、決定性は保たれる。
/// </para>
/// <para>
/// <b>受け渡しは要求ごとに独立させてある。</b> 当初は「要求を知らせるセマフォ」と「完了を知らせる
/// セマフォ」の 2 本で組んでいたが、<b>停止の合図を要求と同じ経路で送っていたため、
/// 停止と要求が重なると要求が 1 つ処理されずに捨てられた</b>（呼び出し側は完了を待ち続けて
/// タイムアウトする）。さらに完了の通知が要求と紐づいていないため、タイムアウトした要求の完了を
/// 次の要求が自分のものとして受け取る余地もあった。<b>この足場が再現したい不具合と同じ型の
/// 欠陥</b>（`ensemble-review.md` §1）なので、要求そのものに完了の合図と結果を持たせる形へ変えた。
/// </para>
/// <para>
/// <b>本番との違い（意図的に残しているもの）</b>:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>レイテンシが 0。</b> <c>WasapiOut.GetPosition()</c> は「再生済み」の位置を返すため、
/// 書き込み位置より要求レイテンシぶん遅れる。こちらは読み出した総量をそのまま返すので、
/// 書き込み位置とほぼ一致する。<c>PlaybackClock</c> のレイテンシ補正は<b>このクラスでは
/// 検証できない</b>
/// </item>
/// <item>
/// <b>読み出しの間隔が現実のデバイスの揺れを持たない。</b> 揺れそのものを試したい場合は
/// <see cref="AdvanceMs"/> の刻みを変えて表現する
/// </item>
/// </list>
/// </remarks>
internal sealed class FakeAudioOutput : IAudioOutput
{
    /// <summary>
    /// 読み出し 1 回の完了を待つ上限。<b>置かないと、読み出し側が詰まったときにテストが
    /// ハングし、ランナーのタイムアウト頼みになって何が壊れたか読めなくなる。</b>
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>レンダースレッドの停止を待つ上限。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Thread _renderThread;
    private readonly BlockingCollection<ReadRequest> _requests = new();

    private IWaveProvider? _provider;
    private byte[] _buffer = [];
    private long _consumedBytes;

    /// <summary>凍結していないことを表す番兵。</summary>
    private const long NotFrozen = -1;
    private long _frozenAtBytes = NotFrozen;

    private bool _playing;
    private bool _disposed;
    /// <summary>
    /// 読み出しが上限内に終わらなかったことのラッチ。<b>一度詰まったら以後の呼び出しも失敗させる</b>
    /// ——詰まったまま次へ進ませると、後続のアサーションが無関係な理由で失敗して原因が読めなくなる。
    /// </summary>
    private string? _stuckReason;

    public FakeAudioOutput(int latencyMs)
    {
        RequestedLatencyMs = latencyMs;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "FakeAudioOutput.Render",
        };
        _renderThread.Start();
    }

    /// <summary><c>MediaEngine</c> が要求したレイテンシ。検証用に覚えているだけ。</summary>
    public int RequestedLatencyMs { get; }

    public WaveFormat OutputWaveFormat => Provider.WaveFormat;

    private IWaveProvider Provider
    {
        get
        {
            lock (_gate)
            {
                return _provider
                    ?? throw new InvalidOperationException("Init がまだ呼ばれていない");
            }
        }
    }

    public bool IsPlaying { get { lock (_gate) return _playing; } }

    /// <summary>これまでに読み出した総バイト数（凍結の影響を受けない実測値）。</summary>
    public long ConsumedBytes { get { lock (_gate) return _consumedBytes; } }

    /// <summary>
    /// 読み出しが詰まった・レンダースレッドが異常終了した場合の理由。無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>表明するのは所有者（<see cref="FakeAudioOutputProvider"/>）。</b> このクラスは
    /// <c>MediaEngine</c> から破棄されるため、<see cref="Dispose"/> で投げると<b>本番の後始末を
    /// 途中で止めてしまう</b>（ネイティブの解放やフィールドの後片付けが飛ぶ）。記録に留める。
    /// </remarks>
    public string? StuckReason { get { lock (_gate) return _stuckReason; } }

    public void Init(IWaveProvider waveProvider)
    {
        lock (_gate)
        {
            if (_provider != null)
                throw new InvalidOperationException("Init は 1 度だけ呼ばれる想定");
            _provider = waveProvider;
        }
    }

    public void Play()
    {
        lock (_gate) _playing = true;
    }

    public void Pause()
    {
        lock (_gate) _playing = false;
    }

    public void Stop()
    {
        lock (_gate) _playing = false;
    }

    public long GetPosition()
    {
        lock (_gate) return _frozenAtBytes == NotFrozen ? _consumedBytes : _frozenAtBytes;
    }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>
    /// 指定した時間ぶんがブロック境界で何バイトになるかを返す。テストが期待値を立てるために使う。
    /// </summary>
    public int BytesForMs(int milliseconds)
    {
        WaveFormat format = OutputWaveFormat;
        int bytes = (int)((long)format.AverageBytesPerSecond * milliseconds / 1000);
        // ブロック境界で切る。端数を渡すとミキサー側が想定しないフレーム数を扱うことになる
        return bytes - bytes % format.BlockAlign;
    }

    /// <summary>
    /// 指定した時間ぶんミキサーから読み出す。
    /// </summary>
    /// <returns>実際に読み出したバイト数。停止・一時停止中は 0（本番でもレンダースレッドは
    /// 止まっているため、これが忠実な振る舞い）。</returns>
    /// <remarks>
    /// <b>「読み出さない」ことで音声の滞留を再現できる</b>——単にこれを呼ばなければよい。
    /// 「読み出すのに位置が進まない」（音も映像も動くのにクロックだけ凍る経路）は
    /// <see cref="FreezePosition"/> を使う。
    /// </remarks>
    public int AdvanceMs(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        int bytes = BytesForMs(milliseconds);

        lock (_gate)
        {
            if (_stuckReason != null) throw new InvalidOperationException(_stuckReason);
            if (_disposed) throw new ObjectDisposedException(nameof(FakeAudioOutput));
            if (!_playing) return 0;
        }

        return bytes <= 0 ? 0 : RequestRead(bytes);
    }

    /// <summary>
    /// 以後 <see cref="GetPosition"/> が現在の値を返し続けるようにする。読み出しは続く。
    /// </summary>
    public void FreezePosition()
    {
        lock (_gate)
        {
            if (_frozenAtBytes == NotFrozen) _frozenAtBytes = _consumedBytes;
        }
    }

    public void UnfreezePosition()
    {
        lock (_gate) _frozenAtBytes = NotFrozen;
    }

    /// <summary>
    /// 異常停止を模す。<paramref name="failure"/> が <c>null</c> なら正常停止として扱われる。
    /// </summary>
    /// <remarks>
    /// 送り主はこのインスタンスにしているが、<b>利用側はそれに依存していない</b>
    /// （<see cref="IAudioOutput.PlaybackStopped"/> の注記。<c>MediaEngine</c> は購読時に掴んだ
    /// 実体を自分で覚えている）。ここを別のオブジェクトに変えても判定は変わらない。
    /// </remarks>
    public void RaisePlaybackStopped(Exception? failure)
        => PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));

    private int RequestRead(int bytes)
    {
        var request = new ReadRequest(bytes);
        try
        {
            _requests.Add(request);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 済み＝破棄が始まっている
            throw new ObjectDisposedException(nameof(FakeAudioOutput));
        }

        if (!request.Done.Wait(ReadTimeout))
        {
            // **既に判明している理由があればそれを使う。** レンダースレッドが例外で死んだ場合、
            // 汎用の「詰まっている」ではなく実際の理由を出さないと、原因を内部まで潜って
            // 調べ直す羽目になる
            string reason;
            lock (_gate)
            {
                reason = _stuckReason ??=
                    $"ミキサーからの読み出しが {ReadTimeout.TotalSeconds} 秒で終わらなかった"
                    + $"（要求 {bytes} バイト）。読み出し側が詰まっている";
            }
            throw new TimeoutException(reason);
        }

        if (request.Failure != null)
            throw new InvalidOperationException("ミキサーの読み出しが例外で終わった", request.Failure);

        return request.Read;
    }

    /// <remarks>
    /// <b>停止は <see cref="BlockingCollection{T}.CompleteAdding"/> で伝える。</b>
    /// 積まれている要求を全部処理してから抜けるため、<b>停止と要求が重なっても要求が捨てられない</b>
    /// （要求と同じ経路で停止を知らせていた当初の実装はここで取りこぼしていた）。
    /// </remarks>
    private void RenderLoop()
    {
        try
        {
            foreach (ReadRequest request in _requests.GetConsumingEnumerable())
                ServeRequest(request);
        }
        catch (Exception ex)
        {
            // **ループの外へ抜けたことを黙って終わらせない。** ここへ来るとレンダースレッドは
            // 死ぬので、待っている側・以後の呼び出しの両方に実際の理由を伝える必要がある
            string reason = $"読み出しスレッドが想定外の例外で終了した: {ex}";
            lock (_gate) _stuckReason ??= reason;

            // **待っている要求を上限まで待たせない。** 積まれている分も含めて理由を付けて返す。
            // ここ自体が失敗する可能性（コレクションが既に破棄されている等）も畳む——
            // バックグラウンドスレッドの未処理例外はプロセスごと落とすので、
            // 「ハング」ではなく「無関係なクラッシュ」という別の壊れ方になる
            try
            {
                _requests.CompleteAdding();
                foreach (ReadRequest pending in _requests.GetConsumingEnumerable())
                {
                    pending.Failure = new InvalidOperationException(reason, ex);
                    pending.Done.Set();
                }
            }
            catch (Exception secondary)
            {
                lock (_gate) _stuckReason += $"（待機者の解放も失敗した: {secondary.Message}）";
            }
        }
    }

    /// <remarks>
    /// <b>例外が出ても <see cref="ReadRequest.Done"/> は必ず立てる。</b> 立てないと待っている
    /// <see cref="RequestRead"/> が上限まで気づけず、しかも得られる理由が「詰まっている」に
    /// なって実際の原因（例外）と食い違う。<c>provider.Read</c> の外側
    /// （バッファ確保など）で出た例外も同じ扱いにするため、<c>try</c> はこの本体全体を覆う。
    /// </remarks>
    private void ServeRequest(ReadRequest request)
    {
        try
        {
            IWaveProvider provider;
            byte[] buffer;
            lock (_gate)
            {
                provider = _provider!;
                if (_buffer.Length < request.Bytes) _buffer = new byte[request.Bytes];
                buffer = _buffer;
            }

            int read = provider.Read(buffer, 0, request.Bytes);

            lock (_gate)
            {
                // 位置は「読み出せた分」だけ進める。ミキサーが要求より少なく返すこともある
                _consumedBytes += read;
            }
            request.Read = read;
        }
        catch (Exception ex)
        {
            request.Failure = ex;
        }
        finally
        {
            request.Done.Set();
        }
    }

    /// <remarks>
    /// <b>ここでは投げない。</b> このクラスを破棄するのは <c>MediaEngine.DisposeDecoders</c> で、
    /// そこから例外を抜くと<b>後始末の残り（ネイティブの <c>AVFormatContext</c> の解放・
    /// フィールドの後片付け・GPU デバイスの解放）が丸ごと飛ぶ</b>。解放されない資源が残り、
    /// 次の <c>Close</c> が同じデコーダを二度破棄しうる（<c>ensemble-review.md</c> §3）。
    /// <b>検出したい不具合を報告する代わりに別の障害を作ることになる。</b>
    /// <para>
    /// 代わりに<b>止まらなかったことを <see cref="StuckReason"/> へ残し、表明は所有者
    /// （<see cref="FakeAudioOutputProvider"/>）が行う</b>。あちらはテストが所有するオブジェクトなので、
    /// 破棄で投げても本番の経路を壊さない。
    /// </para>
    /// <para>
    /// <see cref="_requests"/> は破棄しない。レンダースレッドが止まらなかった場合、
    /// そのスレッドがまだ列挙しているため（破棄すると別の例外に化ける）。
    /// テストプロセスの終了で回収される。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _playing = false;
        }

        _requests.CompleteAdding();
        if (!_renderThread.Join(ShutdownTimeout))
        {
            lock (_gate)
            {
                _stuckReason ??=
                    $"読み出しスレッドが {ShutdownTimeout.TotalSeconds} 秒で停止しなかった。"
                    + "ミキサーの Read が戻っていない可能性がある";
            }
            return;
        }

        _requests.Dispose();
    }

    /// <summary>
    /// 読み出し 1 回分の要求。<b>完了の合図と結果を要求自身が持つ</b>ので、
    /// 別の要求の完了を取り違えることがない。
    /// </summary>
    private sealed class ReadRequest(int bytes)
    {
        public int Bytes { get; } = bytes;

        public int Read { get; set; }

        public Exception? Failure { get; set; }

        /// <remarks>
        /// <b>意図して破棄していない。</b> タイムアウトで抜けた要求は、レンダースレッドが
        /// 後から <c>Set()</c> しうるため、待機側が破棄すると<b>そちらが例外に化ける</b>。
        /// 要求 1 件ごとに 1 つで、寿命はテストプロセスに閉じるため積み上がっても問題にならない
        /// （通常はスピンで完結し、カーネルのハンドルまで作られない）。
        /// </remarks>
        public ManualResetEventSlim Done { get; } = new(false);
    }
}
