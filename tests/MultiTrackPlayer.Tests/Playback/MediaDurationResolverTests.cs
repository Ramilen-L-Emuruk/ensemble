using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Tests.Playback;

/// <summary>
/// 尺の決定（コンテナ申告 → ストリーム申告 → 不明）。
/// 実際に踏んだ不具合は「短い音声ファイルでシークバーのつまみが動かない」で、
/// 原因はコンテナ申告の 0 や <c>AV_NOPTS_VALUE</c> を検査せずに使っていたこと。
/// </summary>
public class MediaDurationResolverTests
{
    [Fact(DisplayName = "コンテナが尺を申告していればそれを使う")]
    public void Resolve_WithUsableContainerDuration_UsesContainer()
    {
        var result = MediaDurationResolver.Resolve(12.5, new[] { 3.0, 4.0 });

        Assert.Equal(DurationSource.Container, result.Source);
        Assert.Equal(12.5, result.Seconds);
        Assert.True(result.IsKnown);
    }

    [Theory(DisplayName = "コンテナ申告が使えない値ならストリームから補完する")]
    [InlineData(double.NaN)]        // AV_NOPTS_VALUE を呼び出し側が NaN へ直して渡す
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NegativeInfinity)]
    public void Resolve_WithUnusableContainerDuration_FallsBackToStreams(double containerSeconds)
    {
        var result = MediaDurationResolver.Resolve(containerSeconds, new[] { 3.0, 4.25 });

        Assert.Equal(DurationSource.Streams, result.Source);
        Assert.Equal(4.25, result.Seconds);
    }

    [Fact(DisplayName = "ストリーム側は最長のものを採る")]
    public void Resolve_WithMultipleStreams_TakesLongest()
    {
        // 音声だけ・映像だけが尺を持つファイルがあるため、最初の要素ではなく最長を採る
        var result = MediaDurationResolver.Resolve(double.NaN, new[] { double.NaN, 9.0, 2.0 });

        Assert.Equal(9.0, result.Seconds);
    }

    [Fact(DisplayName = "ストリーム側も使える値が無ければ不明とする")]
    public void Resolve_WithNoUsableValues_IsUnknown()
    {
        var result = MediaDurationResolver.Resolve(double.NaN, new[] { double.NaN, 0.0, -5.0 });

        Assert.Equal(DurationSource.Unknown, result.Source);
        Assert.Equal(0.0, result.Seconds);
        Assert.False(result.IsKnown);
    }

    [Fact(DisplayName = "ストリームの一覧が空でも不明として扱う")]
    public void Resolve_WithEmptyStreams_IsUnknown()
    {
        var result = MediaDurationResolver.Resolve(double.NaN, Array.Empty<double>());

        Assert.Equal(DurationSource.Unknown, result.Source);
    }

    [Fact(DisplayName = "ストリームの一覧が null でも例外にしない")]
    public void Resolve_WithNullStreams_IsUnknown()
    {
        // 呼び出し側は unsafe な FFmpeg 構造体から組み立てるため、null を渡しうる形にしておく
        var result = MediaDurationResolver.Resolve(double.NaN, null);

        Assert.Equal(DurationSource.Unknown, result.Source);
    }

    [Fact(DisplayName = "不明のとき 0 を返すが、0 秒のメディアとは区別できる")]
    public void Resolve_Unknown_IsDistinguishableFromZeroLengthMedia()
    {
        var unknown = MediaDurationResolver.Resolve(double.NaN, null);
        var zeroLength = MediaDurationResolver.Resolve(0.0, new[] { 0.0 });

        // どちらも Seconds は 0 だが、Source で区別できる（記録の強さを分けるために要る）
        Assert.Equal(0.0, unknown.Seconds);
        Assert.Equal(0.0, zeroLength.Seconds);
        Assert.Equal(DurationSource.Unknown, unknown.Source);
        Assert.Equal(DurationSource.Unknown, zeroLength.Source);
    }

    [Theory(DisplayName = "TimeSpan で表せない巨大な尺は使わない（壊れたヘッダ）")]
    [InlineData(double.MaxValue)]
    [InlineData(1e15)]
    public void Resolve_WithUnrepresentableContainerDuration_FallsBackToStreams(double containerSeconds)
    {
        // 通すと呼び出し側の TimeSpan.FromSeconds が例外になり、ファイルが開けなくなる
        var result = MediaDurationResolver.Resolve(containerSeconds, new[] { 7.0 });

        Assert.Equal(DurationSource.Streams, result.Source);
        Assert.Equal(7.0, result.Seconds);
    }

    [Fact(DisplayName = "巨大な尺はストリーム側でも使わない")]
    public void Resolve_WithUnrepresentableStreamDuration_IsUnknown()
    {
        var result = MediaDurationResolver.Resolve(double.NaN, new[] { double.MaxValue, 1e15 });

        Assert.Equal(DurationSource.Unknown, result.Source);
    }

    [Fact(DisplayName = "採用した尺は必ず TimeSpan へ直せる")]
    public void Resolve_UsableResult_IsAlwaysConvertibleToTimeSpan()
    {
        // このクラスの役目は「呼び出し側が TimeSpan.FromSeconds を安全に呼べる値だけを返す」こと。
        // 上限の余裕（丸め上がり対策）が効いていることを、実際に変換して確かめる
        double justBelowLimit = TimeSpan.MaxValue.TotalSeconds - 1.0;
        var result = MediaDurationResolver.Resolve(justBelowLimit, null);

        Assert.True(result.IsKnown);
        _ = TimeSpan.FromSeconds(result.Seconds); // 例外が出れば失敗
    }

    [Fact(DisplayName = "極端に短い尺も使える値として扱う")]
    public void Resolve_WithVeryShortDuration_IsUsable()
    {
        // 実際に踏んだのは数秒のファイル。短いこと自体は異常ではない
        var result = MediaDurationResolver.Resolve(0.05, null);

        Assert.Equal(DurationSource.Container, result.Source);
        Assert.Equal(0.05, result.Seconds);
    }
}
