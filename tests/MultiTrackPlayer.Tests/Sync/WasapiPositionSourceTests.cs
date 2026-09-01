using MultiTrackPlayer.Engine.Sync;
using NAudio.Wave;

namespace MultiTrackPlayer.Tests.Sync;

/// <summary>
/// ハードウェア位置の検査とフォールバック切替。<c>IWavePosition</c> を差し替えられるので、
/// 実時間も実デバイスも要らない。
/// </summary>
public class WasapiPositionSourceTests
{
    private const int SampleRate = 48000;
    private const int ViolationThreshold = 5;

    /// <summary>write cursor の上限判定（<c>frames &lt;= writeCursor + sampleRate</c>）を常に満たす値。</summary>
    private const long UnboundedWriteCursor = long.MaxValue / 2;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    /// <summary>1 秒ぶんのバイト数。これを単位に動かすと、写像の丸め誤差を気にせず値を主張できる。</summary>
    private static long BytesPerSecond => Format.AverageBytesPerSecond;

    private sealed class FakeWavePosition : IWavePosition
    {
        private long _bytes;

        /// <summary>0 以外なら、<see cref="GetPosition"/> が呼ばれるたびにこのバイト数だけ進む。</summary>
        public long AdvancePerCall { get; init; }

        public WaveFormat OutputWaveFormat => Format;

        public long Bytes
        {
            get => Volatile.Read(ref _bytes);
            set => Volatile.Write(ref _bytes, value);
        }

        public long GetPosition()
            => AdvancePerCall == 0 ? Volatile.Read(ref _bytes) : Interlocked.Add(ref _bytes, AdvancePerCall);
    }

    private static WasapiPositionSource Create(FakeWavePosition fake, long writeCursor = UnboundedWriteCursor)
        => new(fake, Format, SampleRate, () => writeCursor, latencySeconds: 0.0);

    [Fact(DisplayName = "バイト位置を mixer サンプル軸のフレーム数へ写像する")]
    public void GetPositionFrames_MapsBytesToMixerFrames()
    {
        var fake = new FakeWavePosition { Bytes = BytesPerSecond * 2 };
        var source = Create(fake);

        Assert.Equal(SampleRate * 2L, source.GetPositionFrames());
    }

    [Fact(DisplayName = "巻き戻ったら直近の正常値を維持する")]
    public void GetPositionFrames_HoldsLastGoodValue_WhenMonotonicityViolated()
    {
        var fake = new FakeWavePosition { Bytes = BytesPerSecond };
        var source = Create(fake);
        Assert.Equal(SampleRate, source.GetPositionFrames());

        fake.Bytes = 0; // ハードウェア位置が巻き戻った

        Assert.Equal(SampleRate, source.GetPositionFrames());
        Assert.False(source.IsFallbackActive);
    }

    [Fact(DisplayName = "違反が閾値まで連続したらフォールバックへ切り替える")]
    public void GetPositionFrames_SwitchesToFallback_AfterConsecutiveViolations()
    {
        var fake = new FakeWavePosition { Bytes = BytesPerSecond };
        var source = Create(fake);
        source.GetPositionFrames();

        fake.Bytes = 0;
        for (int i = 0; i < ViolationThreshold - 1; i++)
        {
            source.GetPositionFrames();
            Assert.False(source.IsFallbackActive);
        }
        source.GetPositionFrames();

        Assert.True(source.IsFallbackActive);
    }

    /// <summary>
    /// 切替の条件は<b>連続</b>であること。1 度でも正常な値が返れば数え直す。
    /// 累積で数えると、長時間の再生で必ずいつか切り替わる。
    /// </summary>
    [Fact(DisplayName = "正常な値が 1 度返れば違反の数えは振り出しに戻る")]
    public void GetPositionFrames_ResetsViolationCount_WhenValueRecovers()
    {
        var fake = new FakeWavePosition { Bytes = BytesPerSecond };
        var source = Create(fake);
        source.GetPositionFrames();

        fake.Bytes = 0;
        for (int i = 0; i < ViolationThreshold - 1; i++) source.GetPositionFrames();

        fake.Bytes = BytesPerSecond * 2; // 正常な値が返る
        Assert.Equal(SampleRate * 2L, source.GetPositionFrames());

        fake.Bytes = 0;
        for (int i = 0; i < ViolationThreshold - 1; i++) source.GetPositionFrames();

        Assert.False(source.IsFallbackActive);
    }

    [Fact(DisplayName = "write cursor を大きく超える値も違反として数える")]
    public void GetPositionFrames_CountsOvershootBeyondWriteCursor_AsViolation()
    {
        // 位置は 10 秒。write cursor は 1 秒ぶんしか進んでいない（許容は +1 秒まで）
        var fake = new FakeWavePosition { Bytes = BytesPerSecond * 10 };
        var source = Create(fake, writeCursor: SampleRate);

        for (int i = 0; i < ViolationThreshold; i++) source.GetPositionFrames();

        Assert.True(source.IsFallbackActive);
    }

    [Fact(DisplayName = "Reset はフォールバックと違反の数えを解除する")]
    public void Reset_ClearsFallbackAndViolations()
    {
        var fake = new FakeWavePosition { Bytes = BytesPerSecond };
        var source = Create(fake);
        source.GetPositionFrames();
        fake.Bytes = 0;
        for (int i = 0; i < ViolationThreshold; i++) source.GetPositionFrames();
        Assert.True(source.IsFallbackActive);

        source.Reset();

        Assert.False(source.IsFallbackActive);
        fake.Bytes = BytesPerSecond * 3;
        Assert.Equal(SampleRate * 3L, source.GetPositionFrames());
    }

    /// <summary>
    /// <b>この検出器は複数スレッドから同時に呼ばれる</b>——映像側（GPU 経路の vout スレッドが
    /// 毎 vsync、または CPU 経路の UI スレッドが毎フレーム）と、状態タイマー（100ms 周期）。
    /// 映像側の 2 つは定常状態では排他だが、過渡状態では重なる
    /// （実装側のクラス remarks に条件を書いてある）。検査状態を守らないと
    /// <b>インターリーブそれ自体が違反を生む</b>: A が位置 X を読み、B が後から Y（&gt;X）を読んで
    /// 先に基準を Y へ更新すると、A の単調性判定が <c>X &gt;= Y</c> で偽になる。
    /// ハードウェアの位置は単調なのに、<b>その呼び出しは新しい値ではなく直近の正常値を返す</b>。
    /// </summary>
    /// <remarks>
    /// 主張は「返る値がすべて異なること」。ロックが効いていれば呼び出しは直列化され、
    /// 各呼び出しは前より大きい値を返す（<c>AdvancePerCall</c> が呼び出しごとに進めるため）。
    /// 違反が起きた呼び出しだけが<b>直近の正常値</b>を返し、重複として現れる。
    /// <see cref="WasapiPositionSource.IsFallbackActive"/> を見るより<b>ずっと感度が高い</b>——
    /// あちらは違反が 5 回<b>連続</b>しないと立たない。
    /// <para>
    /// <b>偽陽性は出ない。</b> ロックが効いている限り重複は原理的に生じない。
    /// 逆に窓を踏み外せば見逃すので、<b>通ったことは保証ではなく反証の不在</b>。
    /// 反復回数は実測で決めた——ロックを外すと 2 万回 × 2 スレッドで 3 回連続して検出できた
    /// （所要 30ms、重複は 4 万回中 1 件）。<b>この 1 件という頻度が、
    /// 「フォールバック切替が起きる」と言えない根拠でもある</b>（切替は 5 回連続が必要）。
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "同時に呼ばれても、正常な位置を違反と数えない")]
    public async Task GetPositionFrames_ConcurrentCallers_DoNotFabricateViolations()
    {
        const int iterationsPerThread = 20_000;

        // 1 呼び出しにつき 1 秒ぶん進める。フレーム数の刻みが大きいので、
        // 写像の丸めで別の呼び出しと同じ値になることがない
        var fake = new FakeWavePosition { AdvancePerCall = BytesPerSecond };
        var source = Create(fake);

        var results = new System.Collections.Concurrent.ConcurrentBag<long>();

        void Hammer()
        {
            for (int i = 0; i < iterationsPerThread; i++) results.Add(source.GetPositionFrames());
        }

        var other = Task.Run(Hammer);
        Hammer();
        await other;

        Assert.Equal(iterationsPerThread * 2, results.Count);
        // 重複が 1 つでもあれば、それは「違反として直近の正常値を返した」呼び出し
        Assert.Equal(results.Count, results.Distinct().Count());
        Assert.False(source.IsFallbackActive);
    }
}
