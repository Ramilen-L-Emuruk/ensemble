using MultiTrackPlayer.Engine;
using Xunit;

namespace MultiTrackPlayer.Tests;

/// <summary>
/// <see cref="SeekEpoch"/> の値としての振る舞いを押さえる。パイプライン全体がこの型の
/// 「等値で比較できる」「辞書のキーになる」「順序が付く」性質に依存しているため、
/// record struct の自動生成に任せている部分も含めて明示的に検証する。
/// </summary>
public sealed class SeekEpochTests
{
    [Fact(DisplayName = "初期世代は 0")]
    public void Initial_IsZero()
    {
        Assert.Equal(0, SeekEpoch.Initial.Value);
    }

    [Fact(DisplayName = "Next は元の値を変えず 1 つ進んだ世代を返す")]
    public void Next_ReturnsIncrementedEpoch_WithoutMutatingSource()
    {
        var epoch = new SeekEpoch(3);

        SeekEpoch next = epoch.Next();

        Assert.Equal(4, next.Value);
        Assert.Equal(3, epoch.Value); // 値型なので元は変わらない
    }

    [Fact(DisplayName = "同じ値の世代は等値")]
    public void Equality_IsByValue()
    {
        Assert.Equal(new SeekEpoch(7), new SeekEpoch(7));
        Assert.NotEqual(new SeekEpoch(7), new SeekEpoch(8));
        Assert.True(new SeekEpoch(7) == new SeekEpoch(7));
        Assert.True(new SeekEpoch(7) != new SeekEpoch(8));
    }

    // デコードスレッドはシーク目標を「世代 → 目標秒」の辞書で保持する。
    // ハッシュが値に基づいていないと、Flush 番兵が自分の目標を引けず
    // プリロールが完了しないまま固まる
    [Fact(DisplayName = "辞書のキーとして値で一致する")]
    public void CanBeUsedAsDictionaryKey()
    {
        var map = new Dictionary<SeekEpoch, double> { [new SeekEpoch(2)] = 12.5 };

        Assert.True(map.TryGetValue(new SeekEpoch(2), out double target));
        Assert.Equal(12.5, target);
        Assert.False(map.ContainsKey(new SeekEpoch(3)));
    }

    // 古い世代宛ての目標値を掃除する処理が「以下」の比較に依存している
    [Theory(DisplayName = "世代には順序が付く")]
    [InlineData(1, 2)]
    [InlineData(0, 9)]
    public void Comparison_FollowsNumericOrder(int smaller, int larger)
    {
        var lo = new SeekEpoch(smaller);
        var hi = new SeekEpoch(larger);
        // 同値どうしの比較は別インスタンスで確かめる（同一変数の比較はコンパイラ警告になる）
        var loAgain = new SeekEpoch(smaller);

        Assert.True(lo < hi);
        Assert.True(lo <= hi);
        Assert.True(hi > lo);
        Assert.True(hi >= lo);
        Assert.True(lo <= loAgain);
        Assert.True(lo >= loAgain);
        Assert.True(lo.CompareTo(hi) < 0);
        Assert.Equal(0, lo.CompareTo(loAgain));
    }

    [Fact(DisplayName = "ログ出力用に世代番号だけを文字列化する")]
    public void ToString_ReturnsBareNumber()
    {
        Assert.Equal("5", new SeekEpoch(5).ToString());
    }
}
