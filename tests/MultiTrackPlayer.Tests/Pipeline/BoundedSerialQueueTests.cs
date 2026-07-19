using MultiTrackPlayer.Engine.Pipeline;

namespace MultiTrackPlayer.Tests.Pipeline;

public sealed class BoundedSerialQueueTests
{
    [Fact]
    public void Put_Get_ReturnsSameValue_InFifoOrder()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        Assert.True(q.Put(1, serial: 0));
        Assert.True(q.Put(2, serial: 0));

        Assert.True(q.Get(out var a));
        Assert.True(q.Get(out var b));

        Assert.Equal(QueueItemKind.Data, a.Kind);
        Assert.Equal(1, a.Value);
        Assert.Equal(2, b.Value);
    }

    [Fact]
    public void Put_Blocks_WhenFull_AndUnblocksAfterGet()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 1);
        Assert.True(q.Put(1, 0));

        var putTask = Task.Run(() => q.Put(2, 0));
        Assert.False(putTask.Wait(200), "Put はキュー満杯中はブロックし続けるべき");

        Assert.True(q.Get(out var first));
        Assert.Equal(1, first.Value);

        Assert.True(putTask.Wait(1000), "Get で空きができたら Put は解放されるべき");
        Assert.True(putTask.Result);
    }

    [Fact]
    public void Get_Blocks_WhenEmpty_AndUnblocksAfterPut()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);

        var getTask = Task.Run(() =>
        {
            q.Get(out var item);
            return item.Value;
        });
        Assert.False(getTask.Wait(200), "Get はキューが空の間はブロックし続けるべき");

        Assert.True(q.Put(42, 0));
        Assert.True(getTask.Wait(1000), "Put されたら Get は解放されるべき");
        Assert.Equal(42, getTask.Result);
    }

    [Fact]
    public void Close_UnblocksPendingPut_AndReturnsFalse()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 1);
        Assert.True(q.Put(1, 0));

        var putTask = Task.Run(() => q.Put(2, 0));
        Assert.False(putTask.Wait(200));

        q.Close();

        Assert.True(putTask.Wait(1000));
        Assert.False(putTask.Result);
    }

    [Fact]
    public void Close_UnblocksPendingGet_AndReturnsFalse_WhenEmpty()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);

        var getTask = Task.Run(() => q.Get(out _));
        Assert.False(getTask.Wait(200));

        q.Close();

        Assert.True(getTask.Wait(1000));
        Assert.False(getTask.Result);
    }

    [Fact]
    public void Close_IsIdempotent()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        Assert.True(q.Close());
        Assert.False(q.Close());
        Assert.True(q.IsClosed);
    }

    [Fact]
    public void Put_ReturnsFalseImmediately_WhenAlreadyClosed()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        q.Close();
        Assert.False(q.Put(1, 0));
    }

    [Fact]
    public void Flush_DisposesQueuedItems_ClearsQueue_AndBumpsSerial()
    {
        var disposed = new List<int>();
        var q = new BoundedSerialQueue<int>(maxCount: 8, disposer: v => disposed.Add(v));
        q.Put(1, 0);
        q.Put(2, 0);

        int newSerial = q.Flush();

        Assert.Equal(1, newSerial);
        Assert.Equal(new[] { 1, 2 }, disposed);

        Assert.True(q.Get(out var marker));
        Assert.Equal(QueueItemKind.Flush, marker.Kind);
        Assert.Equal(1, marker.Serial);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void PutEof_IsDequeued_AsEofKind_WithGivenSerial()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        q.PutEof(serial: 5);

        Assert.True(q.Get(out var item));
        Assert.Equal(QueueItemKind.Eof, item.Kind);
        Assert.Equal(5, item.Serial);
    }

    [Fact]
    public void WeightLimit_BlocksPut_WhenCumulativeWeightAlreadyAtOrOverLimit()
    {
        // 判定は「投入前」の累積重みに対して行う（投入する値自体の重みは考慮しない）ため、
        // 上限をわずかに超えることはあるが、それ以降の Put は重みが下がるまでブロックする。
        var q = new BoundedSerialQueue<int>(maxCount: 100, maxWeight: 10, weigh: v => v);
        Assert.True(q.Put(8, 0)); // weight=8 (<10) → 通る
        Assert.True(q.Put(5, 0)); // 投入前の weight=8 (<10) だったので通る。結果 weight=13

        var putTask = Task.Run(() => q.Put(1, 0)); // 投入前の weight=13 (>=10) でブロックするはず
        Assert.False(putTask.Wait(200));

        Assert.True(q.Get(out _)); // weight 8 を引く → 5 (<10)
        Assert.True(putTask.Wait(1000));
    }

    [Fact]
    public void WeightLimit_NeverDeadlocks_OnSingleOversizedItem()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 100, maxWeight: 1, weigh: v => v);
        // 単独アイテムの重みが上限を超えていても、キューが空なら即座に通す
        var putTask = Task.Run(() => q.Put(99, 0));
        Assert.True(putTask.Wait(1000));
        Assert.True(putTask.Result);
    }
}
