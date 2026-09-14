using MultiTrackPlayer.Engine.Decoding;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Tests.Decoding;

/// <summary>
/// <c>avcodec_receive_frame</c> の戻り値の分類。ネイティブ呼び出しを含まない純粋な写像なので、
/// FFmpeg のネイティブライブラリを読み込まずに検証できる。
/// </summary>
/// <remarks>
/// この分類を切り出した理由は <see cref="ReceiveOutcome"/> の remarks——以前は <c>bool</c> に
/// 畳んでいたため「送信は成功し続けるのに受信が失敗し続ける」状態を呼び出し側が検出できなかった。
/// <b>ここで固定したいのは「正常な空振りと本物のエラーの境目」</b>で、そこがずれると
/// 前進不能の判定が空振りするか、正常な再生を異常と呼ぶかのどちらかになる。
/// </remarks>
public class ReceiveOutcomeClassifierTests
{
    [Fact(DisplayName = "0 はフレームを取り出せたことを表す")]
    public void Classify_Zero_IsFrame()
    {
        Assert.Equal(ReceiveOutcome.Frame, ReceiveOutcomeClassifier.Classify(0));
    }

    /// <summary>
    /// 入力が足りないだけ。リオーダ遅延で普通に起きるので、これをエラーと数えると
    /// 正常な再生を異常と呼ぶことになる。
    /// </summary>
    [Fact(DisplayName = "-EAGAIN は正常な空振り")]
    public void Classify_Eagain_IsAgain()
    {
        Assert.Equal(ReceiveOutcome.Again, ReceiveOutcomeClassifier.Classify(-EAGAIN));
    }

    [Fact(DisplayName = "AVERROR_EOF はドレイン完了")]
    public void Classify_Eof_IsEndOfStream()
    {
        Assert.Equal(ReceiveOutcome.EndOfStream, ReceiveOutcomeClassifier.Classify(AVERROR_EOF));
    }

    /// <summary>
    /// <b>「知っているエラーコードを列挙して、それ以外を正常にする」形にしないこと</b>の回帰テスト。
    /// 知らないエラーが増えたときに黙って正常扱いになると、前進不能の判定が永久に空振りする
    /// （<c>ensemble-review.md</c> §7 の代理値と同じ穴）。
    /// </summary>
    [Theory(DisplayName = "-EAGAIN と AVERROR_EOF 以外の負値はすべてエラー")]
    [InlineData(-1)]
    [InlineData(-22)]      // EINVAL 相当
    [InlineData(-12)]      // ENOMEM 相当
    [InlineData(-1094995529)] // AVERROR_INVALIDDATA
    [InlineData(int.MinValue)]
    public void Classify_OtherNegative_IsError(int ret)
    {
        // 万一 -EAGAIN / AVERROR_EOF と衝突する値を入れてしまっていたらテストの前提が崩れるので確かめる
        Assert.NotEqual(-EAGAIN, ret);
        Assert.NotEqual(AVERROR_EOF, ret);

        Assert.Equal(ReceiveOutcome.Error, ReceiveOutcomeClassifier.Classify(ret));
    }

    /// <summary>
    /// <c>avcodec_receive_frame</c> は成功時に 0 しか返さない仕様だが、正の値が来ても
    /// 「フレームが取れた」と誤解させないことを固定する。
    /// </summary>
    [Theory(DisplayName = "正の値はフレーム扱いにしない")]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Classify_Positive_IsNotFrame(int ret)
    {
        Assert.NotEqual(ReceiveOutcome.Frame, ReceiveOutcomeClassifier.Classify(ret));
    }

    /// <summary>
    /// 正常な 3 つと異常の境目が、実際の定数の値と地続きであることの確認。
    /// <c>-EAGAIN</c> は環境で値が変わりうるので、値をテストに焼き込まない。
    /// </summary>
    [Fact(DisplayName = "正常として扱うのは 0・-EAGAIN・AVERROR_EOF の 3 つだけ")]
    public void Classify_OnlyThreeValuesAreNotError()
    {
        int[] normal = [0, -EAGAIN, AVERROR_EOF];

        foreach (int ret in normal)
            Assert.NotEqual(ReceiveOutcome.Error, ReceiveOutcomeClassifier.Classify(ret));

        // 3 つの近傍を走査して、他に正常扱いされる値が無いことを確かめる
        foreach (int baseValue in normal)
        {
            for (int delta = -3; delta <= 3; delta++)
            {
                int ret = baseValue + delta;
                if (normal.Contains(ret)) continue;
                Assert.Equal(ReceiveOutcome.Error, ReceiveOutcomeClassifier.Classify(ret));
            }
        }
    }
}
