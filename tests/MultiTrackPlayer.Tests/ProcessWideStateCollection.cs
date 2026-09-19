namespace MultiTrackPlayer.Tests;

/// <summary>
/// プロセス全体で 1 つしかないものに触るテストクラスを直列化するための名前。
/// </summary>
/// <remarks>
/// <b>テストランナーはクラス単位で並列に走る。</b> 同じ名前を付けたクラスどうしは直列になる。
/// いま直列化している理由は 2 つある。
/// <list type="number">
/// <item>
/// <b>診断ログの書き込み先。</b> <c>DiagnosticLog.WriteFatal</c> は「セッションログが開いていれば
/// そちら、無ければ <c>fatal.log</c>」という二段構えで、<b>どちらへ書かれるかを決めるのは
/// <c>Enable</c> / <c>Disable</c> を呼んだ誰か</b>。並列に走る別のクラスがそれを切り替えると、
/// 記録先を前提にしたテストが<b>実装とは無関係な理由で落ちる</b>
/// </item>
/// <item>
/// <b>共有 D3D11 デバイスを作るテスト。</b> 参照数の釣り合いが崩れると、症状は
/// アサーション失敗ではなく<b>テストホストの異常終了</b>として出る。並列に走らせると
/// <b>無関係なテストまで道連れになり、何が壊れたのか読めなくなる</b>
/// </item>
/// </list>
/// <para>
/// 属している（＝直列化される）のは次のクラス。**上記のどちらかに触るテストを新しく書くときは、
/// ここへ加えること。**
/// </para>
/// <list type="bullet">
/// <item><c>Diagnostics.DiagnosticLogTests</c> — 理由 1（<c>Enable</c> で書き込み先を差し替える側）</item>
/// <item><c>Integration.MediaEnginePlaybackTests</c> — 理由 1（<c>fatal.log</c> への記録を検証する側）と理由 2（映像付きファイルを開く）</item>
/// <item><c>Integration.SharedGpuDeviceLifetimeTests</c> — 理由 2</item>
/// <item><c>Integration.VideoPipelineTests</c> — 理由 2</item>
/// <item><c>Integration.SeekTests</c> — 理由 2</item>
/// <item><c>Integration.StallDetectionTests</c> — 理由 1（滞留の記録を検証する側）と理由 2</item>
/// </list>
/// <para>
/// <b>この一覧は属性の代わりではない。</b> 直列化を成立させているのは各クラスの
/// <c>[Collection]</c> 属性で、ここは「なぜ直列でなければならないか」を理由つきで残す場所。
/// <b>付け忘れたときの症状はアサーション失敗ではなく、テストホストの異常終了か
/// 無関係なテストの不安定化</b>——だから理由の側を読めるようにしてある。
/// </para>
/// <para>
/// <b>理由ごとにコレクションを分けられない。</b> xUnit ではクラスが属せるコレクションは 1 つだけで、
/// <c>MediaEnginePlaybackTests</c> は両方に該当する。そのため 1 つにまとめ、名前は
/// 「プロセス全体で 1 つしかないものに触る」という共通点で付けてある
/// （以前は <c>DiagnosticLogCollection</c> という名前だったが、GPU の理由が加わって名前が事実と
/// 食い違うようになったため改名した）。
/// </para>
/// </remarks>
public static class ProcessWideStateCollection
{
    public const string Name = "ProcessWideState";
}
