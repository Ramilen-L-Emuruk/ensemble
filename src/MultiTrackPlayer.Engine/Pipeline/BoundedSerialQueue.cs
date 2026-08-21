using System.Threading;

namespace MultiTrackPlayer.Engine.Pipeline;

public enum QueueItemKind
{
    Data,
    Flush,
    Eof
}

public readonly struct QueueItem<T>
{
    public QueueItemKind Kind { get; }
    public T Value { get; }
    /// <summary>この項目が属するシーク世代。消費側は現在の世代と<b>等値</b>で突き合わせる。</summary>
    public SeekEpoch Epoch { get; }

    private QueueItem(QueueItemKind kind, T value, SeekEpoch epoch)
    {
        Kind = kind;
        Value = value;
        Epoch = epoch;
    }

    public static QueueItem<T> Data(T value, SeekEpoch epoch) => new(QueueItemKind.Data, value, epoch);
    public static QueueItem<T> Flush(SeekEpoch epoch) => new(QueueItemKind.Flush, default!, epoch);
    public static QueueItem<T> Eof(SeekEpoch epoch) => new(QueueItemKind.Eof, default!, epoch);
}

/// <summary>
/// シーク世代（<see cref="SeekEpoch"/>）と Flush/EOF 番兵を持つ有界ブロッキングキュー。
/// Flush はキュー所有スレッド（プロデューサ = demux スレッド）自身が呼ぶ想定で、
/// 内部で待機しないため「Put でブロック中に自分の Flush を待つ」自己デッドロックが構造的に起きない。
/// Close() は待機中の Put/Get を全て解放し、以後の待機を発生させない。
///
/// <para>
/// 世代はこのクラスが自分で進めるのではなく、<see cref="Flush"/> の引数として外から与えられる。
/// 採番するのは <see cref="DemuxThread.RequestSeek"/> だけ（理由は <see cref="SeekEpoch"/> 参照）。
/// </para>
/// </summary>
public sealed class BoundedSerialQueue<T>
{
    private readonly Queue<QueueItem<T>> _queue = new();
    private readonly object _lock = new();
    private readonly int _maxCount;
    private readonly int _maxWeight;
    private readonly Func<T, int> _weigh;
    private readonly Action<T>? _disposer;
    private int _currentWeight;
    private SeekEpoch _epoch = SeekEpoch.Initial;
    private bool _closed;
    // AbortPutWaiters のたびに増える別軸のカウンタ（シーク世代とは無関係）。
    // 満杯待ち中の Put はこれが変わったら false で戻る
    private int _abortGeneration;

    public BoundedSerialQueue(int maxCount, int maxWeight = int.MaxValue, Func<T, int>? weigh = null, Action<T>? disposer = null)
    {
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount));
        _maxCount = maxCount;
        _maxWeight = maxWeight;
        _weigh = weigh ?? (_ => 1);
        _disposer = disposer;
    }

    public int Count { get { lock (_lock) return _queue.Count; } }
    /// <summary>現在のシーク世代（最後に <see cref="Flush"/> で設定された値）。</summary>
    public SeekEpoch Epoch { get { lock (_lock) return _epoch; } }
    public bool IsClosed { get { lock (_lock) return _closed; } }

    /// <summary>待機中の Put/Get を全て解放する。二重呼び出しは無視され false を返す。</summary>
    public bool Close()
    {
        lock (_lock)
        {
            if (_closed) return false;
            _closed = true;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>
    /// 満杯の間はブロックする。Close() または AbortPutWaiters() で中断されたら false を返して即座に戻る
    /// （どちらで戻ったかは IsClosed で判別できる）。
    /// </summary>
    public bool Put(T value, SeekEpoch epoch)
    {
        lock (_lock)
        {
            int abortGen = _abortGeneration;
            while (!_closed && IsFullLocked() && abortGen == _abortGeneration)
                Monitor.Wait(_lock);
            if (_closed || abortGen != _abortGeneration) return false;
            EnqueueLocked(QueueItem<T>.Data(value, epoch));
            return true;
        }
    }

    /// <summary>
    /// 満杯待ちでブロック中の Put を false で中断させる（キュー自体は開いたまま）。
    /// プロデューサ（demux スレッド）が Put でブロックしているとシーク要求のチェックに戻れないため、
    /// シーク要求側がこれを呼んでプロデューサをループ先頭へ帰還させる。
    /// </summary>
    public void AbortPutWaiters()
    {
        lock (_lock)
        {
            _abortGeneration++;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>EOF 番兵は容量を無視して即座に投入する（メタデータのみで実データを持たないため）。</summary>
    public void PutEof(SeekEpoch epoch)
    {
        lock (_lock)
        {
            if (_closed) return;
            EnqueueLocked(QueueItem<T>.Eof(epoch));
        }
    }

    /// <summary>
    /// 滞留中のデータ項目を disposer で破棄しつつキューを空にし、世代を <paramref name="epoch"/> へ
    /// 更新して Flush 番兵を投入する。待機しないため呼び出しスレッドをブロックしない。
    ///
    /// <para>
    /// <b>1 つの世代につき有効な Flush は 1 回だけ</b>。現在と同じか古い世代での呼び出しは
    /// 何も変更せず <c>false</c> を返す（<see cref="Video.SlotSequencer.Flush"/> と対称）。
    /// 受け入れてしまうと同じ世代の Flush 番兵が二度積まれ、消費側の <c>HandleFlush</c> が
    /// 二度走る。2 回目は保留中のシーク目標が 1 回目で回収済みのため見つからず、
    /// <b>進行中のプリロールを「目標なし」として打ち切ってしまう</b>。その結果
    /// <c>MultiTrackMixer.HoldOutput</c> が永久に解除されず、音も映像も出ないまま固まる。
    /// </para>
    /// <para>
    /// 採番が単調増加（<see cref="DemuxThread.RequestSeek"/> が唯一の採番元）である限り、
    /// 正当な新しいシークの世代が現在の世代以下になることはないため、このガードが正当な
    /// Flush を弾くことはない。発火するのは呼び出し規約が破られたときだけ。
    /// </para>
    /// </summary>
    /// <param name="epoch">
    /// このシークで採番された世代。自前でインクリメントせず外から受け取ることで、
    /// 消費側が「次の世代 = 現在 + 1」を予測する必要がなくなる。
    /// </param>
    /// <returns>
    /// 適用した場合 true。現在と同じか古い世代で呼ばれて無視した場合 false
    /// （呼び出し側は呼び出し規約違反として記録すること）。
    /// </returns>
    public bool Flush(SeekEpoch epoch)
    {
        lock (_lock)
        {
            if (epoch <= _epoch) return false;
            if (_disposer != null)
            {
                foreach (var item in _queue)
                    if (item.Kind == QueueItemKind.Data)
                        _disposer(item.Value);
            }
            _queue.Clear();
            _currentWeight = 0;
            _epoch = epoch;
            EnqueueLocked(QueueItem<T>.Flush(epoch));
            return true;
        }
    }

    /// <summary>空の間はブロックする。Close() 済みかつ空なら false を返す。</summary>
    public bool Get(out QueueItem<T> item)
    {
        lock (_lock)
        {
            while (_queue.Count == 0 && !_closed)
                Monitor.Wait(_lock);
            if (_queue.Count == 0)
            {
                item = default;
                return false;
            }
            item = _queue.Dequeue();
            if (item.Kind == QueueItemKind.Data)
                _currentWeight -= _weigh(item.Value);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    private bool IsFullLocked() =>
        _queue.Count >= _maxCount || (_queue.Count > 0 && _currentWeight >= _maxWeight);

    private void EnqueueLocked(QueueItem<T> item)
    {
        _queue.Enqueue(item);
        if (item.Kind == QueueItemKind.Data)
            _currentWeight += _weigh(item.Value);
        Monitor.PulseAll(_lock);
    }
}
