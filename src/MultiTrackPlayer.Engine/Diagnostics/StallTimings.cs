namespace MultiTrackPlayer.Engine.Diagnostics;

/// <summary>
/// 滞留検出の閾値と猶予をまとめたもの。<b>値を運ぶだけで、判断は持たない。</b>
/// </summary>
/// <remarks>
/// <b>これがあるのは、自動テストが実時間を待たずに滞留検出を踏めるようにするため。</b>
/// 本番の値は 3〜5 秒で、3 つの検出器をそのまま待つとテスト全体の所要時間が桁で変わる。
/// <para>
/// <b>値そのものと、その値にした根拠は <c>MediaEngine</c> の各定数の doc が単一の情報源。</b>
/// ここへ書き写さないこと——二重管理になり、片方だけ直したときに食い違う。
/// </para>
/// <para>
/// <b>本番の経路はこの型を明示しない。</b> <c>MediaEngine</c> の既定コンストラクタが
/// 自分の定数から組み立てるので、<b>差し替えるのはテストだけ</b>という関係が保たれる。
/// </para>
/// </remarks>
/// <param name="AudioThresholdMs">音声出力の <c>Read</c> が来ないことを滞留と判定する閾値。</param>
/// <param name="VideoThresholdMs">映像フレームの提示が無いことを滞留と判定する閾値。</param>
/// <param name="ClockThresholdMs">再生位置が進まないことを滞留と判定する閾値。</param>
/// <param name="PrerollGraceMs">
/// シーク後の待ちを「正常」として扱う猶予。<b>プリロール待ちとシーク着地待ちで共用する</b>
/// （共用の理由と、締切を置き直すときの制約は <c>MediaEngine.IsWithinSeekGrace</c> の doc）。
/// </param>
internal sealed record StallTimings(
    int AudioThresholdMs,
    int VideoThresholdMs,
    int ClockThresholdMs,
    int PrerollGraceMs);
