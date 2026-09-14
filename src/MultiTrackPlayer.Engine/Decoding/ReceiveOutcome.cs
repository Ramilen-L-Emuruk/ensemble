using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Decoding;

/// <summary>
/// <c>avcodec_receive_frame</c> の結果。<b>正常な空振りと本物のエラーを区別するための型。</b>
/// </summary>
/// <remarks>
/// 以前は <c>bool</c> に畳んでいたため、<c>-EAGAIN</c>（入力が足りない）・<c>AVERROR_EOF</c>
/// （ドレイン完了）・本物のデコードエラーが呼び出し側から区別できなかった。結果、
/// <b>「送信は成功し続けるのに受信が失敗し続ける」状態を誰も検出できなかった</b>——
/// 送信が通ると前進不能の連続数が振り出しに戻るため、エラーが積み上がらない。
/// <para>
/// <b>枚数では代用できない。</b> 送信が成功しても 1 枚も出ないのはリオーダ遅延で正常に起きる
/// （<c>ensemble-review.md</c> §7 の代理値）。区別するには戻り値の契約を変えるしかない。
/// </para>
/// </remarks>
public enum ReceiveOutcome
{
    /// <summary>1 枚取り出せた。</summary>
    Frame,

    /// <summary>まだ出せない（入力が足りない）。<b>正常</b>——リオーダ遅延で普通に起きる。</summary>
    Again,

    /// <summary>ドレインが終わった。<b>正常</b>。</summary>
    EndOfStream,

    /// <summary>本物のデコードエラー。</summary>
    Error
}

/// <summary>
/// <c>avcodec_receive_frame</c> の戻り値を <see cref="ReceiveOutcome"/> へ写す。
/// </summary>
/// <remarks>
/// デコーダ本体から切り離してあるのは<b>テストできるようにするため</b>
/// （<c>ensemble-review.md</c> §5）。ネイティブ呼び出しを含まない純粋な写像なので、
/// FFmpeg のネイティブライブラリを読み込まずに分類の正しさを固定できる。
/// </remarks>
public static class ReceiveOutcomeClassifier
{
    /// <param name="ret"><c>avcodec_receive_frame</c> の戻り値。</param>
    /// <remarks>
    /// <b><c>-EAGAIN</c> と <c>AVERROR_EOF</c> だけを正常として扱い、残りはすべて
    /// <see cref="ReceiveOutcome.Error"/>。</b> 「知っているエラーコードを列挙して、
    /// それ以外を正常にする」形にしないこと——知らないエラーが増えたときに黙って
    /// 正常扱いになる（<c>ensemble-review.md</c> §7 の代理値と同じ穴）。
    /// </remarks>
    public static ReceiveOutcome Classify(int ret)
    {
        if (ret == 0) return ReceiveOutcome.Frame;
        if (ret == -EAGAIN) return ReceiveOutcome.Again;
        if (ret == AVERROR_EOF) return ReceiveOutcome.EndOfStream;
        return ReceiveOutcome.Error;
    }
}
