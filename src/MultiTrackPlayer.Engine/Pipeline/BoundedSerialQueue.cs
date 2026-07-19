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
    public int Serial { get; }

    private QueueItem(QueueItemKind kind, T value, int serial)
    {
        Kind = kind;
        Value = value;
        Serial = serial;
    }

    public static QueueItem<T> Data(T value, int serial) => new(QueueItemKind.Data, value, serial);
    public static QueueItem<T> Flush(int serial) => new(QueueItemKind.Flush, default!, serial);
    public static QueueItem<T> Eof(int serial) => new(QueueItemKind.Eof, default!, serial);
}

/// <summary>
/// serial 番号と Flush/EOF 番兵を持つ有界ブロッキングキュー。
/// Flush はキュー所有スレッド（プロデューサ = demux スレッド）自身が呼ぶ想定で、
/// 内部で待機しないため「Put でブロック中に自分の Flush を待つ」自己デッドロックが構造的に起きない。
/// Close() は待機中の Put/Get を全て解放し、以後の待機を発生させない。
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
    private int _serial;
    private bool _closed;

    public BoundedSerialQueue(int maxCount, int maxWeight = int.MaxValue, Func<T, int>? weigh = null, Action<T>? disposer = null)
    {
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount));
        _maxCount = maxCount;
        _maxWeight = maxWeight;
        _weigh = weigh ?? (_ => 1);
        _disposer = disposer;
    }

    public int Count { get { lock (_lock) return _queue.Count; } }
    public int Serial { get { lock (_lock) return _serial; } }
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

    /// <summary>満杯の間はブロックする。Close() されたら false を返して即座に戻る。</summary>
    public bool Put(T value, int serial)
    {
        lock (_lock)
        {
            while (!_closed && IsFullLocked())
                Monitor.Wait(_lock);
            if (_closed) return false;
            EnqueueLocked(QueueItem<T>.Data(value, serial));
            return true;
        }
    }

    /// <summary>EOF 番兵は容量を無視して即座に投入する（メタデータのみで実データを持たないため）。</summary>
    public void PutEof(int serial)
    {
        lock (_lock)
        {
            if (_closed) return;
            EnqueueLocked(QueueItem<T>.Eof(serial));
        }
    }

    /// <summary>
    /// 滞留中のデータ項目を disposer で破棄しつつキューを空にし、serial を進めて Flush 番兵を投入する。
    /// 待機しないため呼び出しスレッドをブロックしない。
    /// </summary>
    public int Flush()
    {
        lock (_lock)
        {
            if (_disposer != null)
            {
                foreach (var item in _queue)
                    if (item.Kind == QueueItemKind.Data)
                        _disposer(item.Value);
            }
            _queue.Clear();
            _currentWeight = 0;
            _serial++;
            EnqueueLocked(QueueItem<T>.Flush(_serial));
            return _serial;
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
