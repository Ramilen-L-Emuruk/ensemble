namespace MultiTrackPlayer.Core.Models;

/// <summary>再生を開始するとき、開始位置をどう決めるか。</summary>
public enum PlaybackStartAction
{
    /// <summary>位置に手を入れない。直前のシークが張った錨をそのまま使う。</summary>
    None,

    /// <summary>位置 0 として錨だけ張る。コンテナの読み取り位置が既に先頭にある場合。</summary>
    AnchorAtStart,

    /// <summary><see cref="PlaybackStartDecision.Target"/> へシークする。</summary>
    SeekTo
}

/// <summary>
/// 再生開始時の開始位置の決め方。
/// </summary>
/// <remarks>
/// <para>
/// 判断材料が 5 つあり、組み合わせを取り違えるとそのまま不具合になる。実際に踏んだものだけでも
/// 「最後まで見た動画をもう一度再生できない」「シークした直後に再生を押すと先頭へ戻される」の
/// 2 つがある。FFmpeg・D3D11 に依存しない形で切り出してテストする
/// （<c>.claude/rules/ensemble-review.md</c> の「5. テスト可能性の設計」）。
/// </para>
/// <para>
/// 実際の動作（シークの発行・錨の要求）は呼び出し側が行う。ここは決めるだけ。
/// </para>
/// </remarks>
/// <param name="Action">行う操作。</param>
/// <param name="Target"><see cref="PlaybackStartAction.SeekTo"/> のときのシーク先。それ以外では未使用。</param>
public readonly record struct PlaybackStartDecision(PlaybackStartAction Action, TimeSpan Target)
{
    /// <summary>開始位置の決め方を求める。</summary>
    /// <param name="wasStopped">再生を押した時点で停止状態だったか。false は一時停止からの再開。</param>
    /// <param name="pipelineWasFresh">
    /// 再生を押した時点でパイプラインが存在しなかったか（＝明示的な停止で畳まれていた）。
    /// 終端到達で停止した場合はパイプラインが生きたまま残るため、これで区別できる。
    /// </param>
    /// <param name="restartFromEof">終端に到達したまま再生を押されたか（保留中のシークが無い場合に限る）。</param>
    /// <param name="rewindSkipped">
    /// 停止時にコンテナの読み取り位置を先頭へ戻せなかったか。戻せていないなら錨を張るだけでは
    /// 表示位置と実際の内容が食い違うため、明示的なシークが必要になる。
    /// </param>
    /// <param name="pendingStart">停止中に受けたシーク位置（無ければ null）。</param>
    public static PlaybackStartDecision Decide(
        bool wasStopped, bool pipelineWasFresh, bool restartFromEof, bool rewindSkipped,
        TimeSpan? pendingStart)
    {
        // 一時停止からの再開は位置に触らない。クロックも読み取り位置も生きている
        if (!wasStopped) return new PlaybackStartDecision(PlaybackStartAction.None, TimeSpan.Zero);

        // 停止中に受けたシーク。利用者が明示した位置なので他のどの事情よりも優先する。
        // 読み取り位置を戻せていない場合（rewindSkipped）も、このシークが同時に解決する
        if (pendingStart is TimeSpan pending)
            return new PlaybackStartDecision(PlaybackStartAction.SeekTo, pending);

        // 終端から再生を押した場合、demux は最後まで読み終えて待機しているだけなので、
        // 先頭へ巻き戻さないと何も起きない。読み取り位置を戻せていない場合も同じ扱い
        if (restartFromEof || rewindSkipped)
            return new PlaybackStartDecision(PlaybackStartAction.SeekTo, TimeSpan.Zero);

        // 畳んだ状態からの新規開始。読み取り位置は停止時に戻してあるので錨を張るだけでよい
        if (pipelineWasFresh)
            return new PlaybackStartDecision(PlaybackStartAction.AnchorAtStart, TimeSpan.Zero);

        // 終端到達で停止した後、手動でシークしてから再生した場合。そのシークが既に正しい錨を
        // 張っているので、ここで 0 秒として上書きしてはいけない
        return new PlaybackStartDecision(PlaybackStartAction.None, TimeSpan.Zero);
    }
}
