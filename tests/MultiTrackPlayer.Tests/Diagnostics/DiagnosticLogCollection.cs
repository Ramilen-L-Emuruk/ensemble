namespace MultiTrackPlayer.Tests.Diagnostics;

/// <summary>
/// <c>DiagnosticLog</c> の静的な状態に触るテストクラスを直列化するための名前。
/// </summary>
/// <remarks>
/// <b>書き込み先がプロセス全体で 1 つしかないため必要。</b> <c>DiagnosticLog.WriteFatal</c> は
/// 「セッションログが開いていればそちら、無ければ <c>fatal.log</c>」という二段構えで、
/// <b>どちらへ書かれるかを決めるのは <c>Enable</c> / <c>Disable</c> を呼んだ誰か</b>。
/// 並列に走る別のクラスがそれを切り替えると、記録先を前提にしたテストが
/// <b>実装とは無関係な理由で落ちる</b>。
/// <para>
/// 属している（＝直列化される）のは次のクラス。**新しく <c>DiagnosticLog</c> の記録先に
/// 依存するテストを書くときは、ここへ加えること。**
/// </para>
/// <list type="bullet">
/// <item><c>DiagnosticLogTests</c>（<c>Enable</c> で書き込み先を差し替える側）</item>
/// <item>
/// <c>Integration.MediaEnginePlaybackTests</c>（<c>fatal.log</c> に記録が残ることを検証する側。
/// 差し替えられていないこと自体が前提になる）
/// </item>
/// </list>
/// </remarks>
public static class DiagnosticLogCollection
{
    public const string Name = "DiagnosticLog";
}
