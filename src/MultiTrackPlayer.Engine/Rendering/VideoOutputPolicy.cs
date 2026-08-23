namespace MultiTrackPlayer.Engine.Rendering;

/// <summary>
/// 映像提示の異常判定に関する共通の方針値。GPU 経路（<see cref="SwapChainVideoPresenter"/> +
/// <c>MediaEngine.VideoOutputLoop</c>）と CPU 経路（<c>D3DImagePresenter</c>）で同じ基準を使うための
/// 単一の情報源。片方だけ調整して二経路が非対称になるのを防ぐために定数を共有する。
/// </summary>
public static class VideoOutputPolicy
{
    /// <summary>
    /// 映像を出せない状態をこの時間まで許容し、超えたら既定でも残る記録とユーザー通知を行う。
    ///
    /// <para>
    /// 回数ではなく時間で判定するのが要点。提示が失敗している間は vsync の待機オブジェクトも
    /// 信号を出さなくなり、ループの歩調が 1 秒周期まで崩れることがある。その状況で「N 回連続」を
    /// 基準にすると意図した時間の何十倍も無言で待つことになり、防ぎたかった
    /// 「映像だけ静かに止まって誰にも気づかれない」症状をかえって長引かせる。
    /// </para>
    /// <para>
    /// 一時的なデバイスロスト（GPU リセット直後等）は自己修復するため、都度通知すると邪魔になる。
    /// この猶予はそれを黙って通すためのもの。
    /// </para>
    /// </summary>
    public const long FailureGraceMs = 2000;
}
