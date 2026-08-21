using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Pipeline;

namespace MultiTrackPlayer.Tests.Pipeline;

public sealed class BoundedSerialQueueTests
{
    private static readonly SeekEpoch E0 = SeekEpoch.Initial;
    private static SeekEpoch E(int value) => new(value);

    [Fact]
    public void Put_Get_ReturnsSameValue_InFifoOrder()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        Assert.True(q.Put(1, E0));
        Assert.True(q.Put(2, E0));

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
        Assert.True(q.Put(1, E0));

        var putTask = Task.Run(() => q.Put(2, E0));
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

        Assert.True(q.Put(42, E0));
        Assert.True(getTask.Wait(1000), "Put されたら Get は解放されるべき");
        Assert.Equal(42, getTask.Result);
    }

    [Fact]
    public void Close_UnblocksPendingPut_AndReturnsFalse()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 1);
        Assert.True(q.Put(1, E0));

        var putTask = Task.Run(() => q.Put(2, E0));
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
        Assert.False(q.Put(1, E0));
    }

    [Fact]
    public void Flush_DisposesQueuedItems_ClearsQueue_AndStampsGivenEpoch()
    {
        var disposed = new List<int>();
        var q = new BoundedSerialQueue<int>(maxCount: 8, disposer: v => disposed.Add(v));
        q.Put(1, E0);
        q.Put(2, E0);

        Assert.True(q.Flush(E(1)));

        Assert.Equal(E(1), q.Epoch);
        Assert.Equal(new[] { 1, 2 }, disposed);

        Assert.True(q.Get(out var marker));
        Assert.Equal(QueueItemKind.Flush, marker.Kind);
        Assert.Equal(E(1), marker.Epoch);
        Assert.Equal(0, q.Count);
    }

    [Fact(DisplayName = "Flush は与えられた世代をそのまま設定する（自分で +1 しない）")]
    public void Flush_UsesGivenEpoch_WithoutIncrementingItself()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 8);
        Assert.Equal(SeekEpoch.Initial, q.Epoch);

        // demux はコアレスで飛び番の世代を渡しうる。キューはそれを素通しすること
        // （キュー側が独自に +1 すると、消費側が「次の世代」を予測できなくなる）
        Assert.True(q.Flush(E(7)));

        Assert.Equal(E(7), q.Epoch);
        Assert.True(q.Get(out var marker));
        Assert.Equal(E(7), marker.Epoch);
    }

    [Fact(DisplayName = "Flush 後の Put は新しい世代を刻める")]
    public void Put_AfterFlush_CarriesNewEpoch()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 8);
        Assert.True(q.Flush(E(3)));
        Assert.True(q.Get(out _)); // Flush 番兵を取り出す

        Assert.True(q.Put(9, q.Epoch));

        Assert.True(q.Get(out var item));
        Assert.Equal(QueueItemKind.Data, item.Kind);
        Assert.Equal(E(3), item.Epoch);
    }

    // 同じ世代で Flush 番兵が二度積まれると、消費側の HandleFlush が二度走る。2 回目は
    // 保留中のシーク目標が 1 回目で回収済みのため見つからず、進行中のプリロールを
    // 「目標なし」として打ち切ってしまう。結果 MultiTrackMixer.HoldOutput が永久に解除されず
    // 音も映像も出ないまま固まる。番兵を積ませないことがこの経路を塞ぐ唯一の手段
    [Fact(DisplayName = "同じ世代での二度目の Flush は番兵を積まない")]
    public void Flush_IgnoresSameEpoch_AndDoesNotEnqueueSecondMarker()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 8);
        Assert.True(q.Flush(E(1)));
        Assert.True(q.Get(out var first));
        Assert.Equal(QueueItemKind.Flush, first.Kind);

        Assert.False(q.Flush(E(1)));

        Assert.Equal(E(1), q.Epoch);
        Assert.Equal(0, q.Count); // 二度目の番兵は積まれていない
    }

    [Fact(DisplayName = "古い世代での Flush は世代を巻き戻さず番兵も積まない")]
    public void Flush_IgnoresOlderEpoch()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 8);
        Assert.True(q.Flush(E(5)));
        Assert.True(q.Get(out _));

        Assert.False(q.Flush(E(2)));

        Assert.Equal(E(5), q.Epoch);
        Assert.Equal(0, q.Count);
    }

    // 古い世代の Flush を受け入れてしまうと、滞留中のデータを破棄したうえで世代が巻き戻り、
    // 新世代として投入済みのパケットが「未来から来た残骸」扱いになる
    [Fact(DisplayName = "無視された Flush は滞留中のデータを破棄しない")]
    public void Flush_DoesNotDisposeItems_WhenIgnored()
    {
        var disposed = new List<int>();
        var q = new BoundedSerialQueue<int>(maxCount: 8, disposer: v => disposed.Add(v));
        Assert.True(q.Flush(E(4)));
        Assert.True(q.Get(out _));
        Assert.True(q.Put(42, E(4)));

        Assert.False(q.Flush(E(4)));

        Assert.Empty(disposed);
        Assert.True(q.Get(out var item));
        Assert.Equal(42, item.Value);
    }

    [Fact]
    public void PutEof_IsDequeued_AsEofKind_WithGivenEpoch()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 4);
        q.PutEof(E(5));

        Assert.True(q.Get(out var item));
        Assert.Equal(QueueItemKind.Eof, item.Kind);
        Assert.Equal(E(5), item.Epoch);
    }

    [Fact]
    public void WeightLimit_BlocksPut_WhenCumulativeWeightAlreadyAtOrOverLimit()
    {
        // 判定は「投入前」の累積重みに対して行う（投入する値自体の重みは考慮しない）ため、
        // 上限をわずかに超えることはあるが、それ以降の Put は重みが下がるまでブロックする。
        var q = new BoundedSerialQueue<int>(maxCount: 100, maxWeight: 10, weigh: v => v);
        Assert.True(q.Put(8, E0)); // weight=8 (<10) → 通る
        Assert.True(q.Put(5, E0)); // 投入前の weight=8 (<10) だったので通る。結果 weight=13

        var putTask = Task.Run(() => q.Put(1, E0)); // 投入前の weight=13 (>=10) でブロックするはず
        Assert.False(putTask.Wait(200));

        Assert.True(q.Get(out _)); // weight 8 を引く → 5 (<10)
        Assert.True(putTask.Wait(1000));
    }

    [Fact]
    public void WeightLimit_NeverDeadlocks_OnSingleOversizedItem()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 100, maxWeight: 1, weigh: v => v);
        // 単独アイテムの重みが上限を超えていても、キューが空なら即座に通す
        var putTask = Task.Run(() => q.Put(99, E0));
        Assert.True(putTask.Wait(1000));
        Assert.True(putTask.Result);
    }

    [Fact]
    public void AbortPutWaiters_UnblocksWaitingPut_WithoutClosingQueue()
    {
        // Arrange: 満杯にして Put をブロックさせる
        var q = new BoundedSerialQueue<int>(maxCount: 1);
        Assert.True(q.Put(1, E0));
        var blocked = Task.Run(() => q.Put(2, E0));
        Assert.False(blocked.Wait(200)); // ブロックしていること

        // Act: シーク割込み相当
        q.AbortPutWaiters();

        // Assert: false で戻るがキューは開いたまま（IsClosed で Close と判別できる）
        Assert.True(blocked.Wait(3000));
        Assert.False(blocked.Result);
        Assert.False(q.IsClosed);

        // 空きができれば以後の Put は普通に成功する（demux がループ先頭からシーク処理後に再開する動き）
        Assert.True(q.Get(out _));
        Assert.True(q.Put(3, E(1)));
    }

    [Fact(DisplayName = "AbortPutWaiters はシーク世代に影響しない（別軸のカウンタ）")]
    public void AbortPutWaiters_DoesNotChangeEpoch()
    {
        var q = new BoundedSerialQueue<int>(maxCount: 2);
        Assert.True(q.Flush(E(4)));

        q.AbortPutWaiters();

        Assert.Equal(E(4), q.Epoch);
    }

    [Fact]
    public void AbortPutWaiters_DoesNotAffect_SubsequentPut()
    {
        // Arrange: 誰も待っていない状態で Abort（demux がブロックしていないタイミングのシーク）
        var q = new BoundedSerialQueue<int>(maxCount: 2);
        q.AbortPutWaiters();

        // Assert: その後の Put は影響を受けない
        Assert.True(q.Put(1, E0));
        Assert.True(q.Put(2, E0));
    }
}
