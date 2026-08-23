namespace MultiTrackPlayer.Core.Models;

/// <summary>
/// プレイリスト上の現在地。「次に再生するファイル」を決める位置だけを扱う
/// （UI の選択・一覧の表示は呼び出し側の責務）。
/// </summary>
/// <remarks>
/// 位置は索引ではなくパスで保持し、一覧から導出する。索引を状態として持つと削除・クリアの
/// たびに追従させる手当てが必要になり、経路が増えるほど漏れる（実際に「削除すると自動送りが
/// 1 本飛ぶ」不具合を出した）。
/// 一覧に同一パスが 2 件入らないことを前提にする。重複を許す変更を入れるならここも見直すこと。
/// 一覧からの削除は必ず <see cref="Remove"/>・<see cref="Clear"/> を通すこと。渡した
/// <see cref="IList{T}"/> を外から直接削除すると、空いた位置の記録が更新されず、
/// 削除当時の位置を返し続ける。
/// </remarks>
public sealed class PlaylistCursor
{
    private readonly IList<string> _files;

    /// <summary>
    /// 現在地のファイルを一覧から削除したときに空いた位置。削除で後続が繰り上がるため、
    /// この位置がそのまま「次のファイル」になる。参照するときは <see cref="Gap"/> を使う。
    /// </summary>
    private int? _gapIndex;

    /// <param name="files">現在地の導出元となる一覧。<see cref="Remove"/>・<see cref="Clear"/> は
    /// この一覧を直接書き換える（呼び出し側が別途削除する必要はない）。</param>
    public PlaylistCursor(IList<string> files) => _files = files;

    /// <summary>
    /// 現在地。最後に開こうとしたファイルを指す（開けたかどうかは問わない）。
    /// 開けなかったファイルにも現在地を置くのは、「次へ」を押し直したときに同じファイルを
    /// 再試行し続けないようにするため。「いま再生しているファイル」とは別の概念。
    /// </summary>
    public string? Path { get; private set; }

    /// <summary>現在地の一覧上の位置。導出値なので削除・クリアに自動で追従する。
    /// 一覧に無いファイルを開いている場合は -1。</summary>
    public int Index => Path is null ? -1 : _files.IndexOf(Path);

    /// <summary>
    /// 有効な「空いた位置」。現在地のファイルが一覧に戻っている場合（削除した同じパスを
    /// 再追加した場合）は位置を導出できるので、記録は無効になる。
    /// <see cref="_gapIndex"/> を直接使うと、再追加後も削除当時の位置を返し続ける。
    /// </summary>
    private int? Gap => Index < 0 ? _gapIndex : null;

    /// <summary>
    /// 次送りの起点が確定しているか。一覧上の位置（<see cref="Index"/>）も、削除で空いた位置の
    /// 記録も無い場合に false になる（＝一度も一覧の中のファイルを開いていない状態）。
    /// 現在地のファイルを削除した直後は <see cref="Index"/> が -1 でも空いた位置が残るため true。
    /// 起点が無いまま次送りすると <see cref="PeekNext"/> が先頭のファイル
    /// （＝再生し終えたファイル自身になりうる）を返し、延々と再生を繰り返す。
    /// </summary>
    public bool HasAdvanceOrigin => Gap is not null || Index >= 0;

    /// <summary>次に再生するファイル。現在地は動かさない
    /// （開こうとした時点で <see cref="MoveTo"/> が呼ばれて動く）。</summary>
    /// <remarks>起点が未確定のときは先頭のファイルを返す（プレイリストから再生を始める操作の
    /// ため）。再生終了後の自動送りでは <see cref="HasAdvanceOrigin"/> を確認してから呼ぶこと。</remarks>
    public string? PeekNext() => FileAt(Gap ?? Index + 1);

    /// <summary>前に戻るファイル。現在地は動かさない。
    /// 起点が未確定のときは前のファイルは無い（次送りと違い、先頭から始めることはしない）。</summary>
    public string? PeekPrevious() => FileAt((Gap ?? Index) - 1);

    private string? FileAt(int index) => index >= 0 && index < _files.Count ? _files[index] : null;

    /// <summary>現在地を指定のファイルへ移す。一覧に無いファイルでもそのまま覚える
    /// （<see cref="Index"/> は -1 になる）。</summary>
    public void MoveTo(string path)
    {
        Path = path;
        _gapIndex = null;
    }

    /// <summary>一覧から 1 件削除し、現在地を保つ。</summary>
    /// <returns>削除した場合 true。一覧に無いパスなら false（何もしない）。</returns>
    public bool Remove(string path)
    {
        int removed = _files.IndexOf(path);
        if (removed < 0) return false;
        bool wasCursor = path == Path;
        _files.RemoveAt(removed);

        if (wasCursor)
            _gapIndex = removed;
        else if (_gapIndex is int gap && removed < gap)
            // 空いた位置より前が消えたので繰り上げる。removed == gap のときは、その後続が
            // 同じ位置へ繰り上がるので調整は要らない。
            // Gap が無効な期間（現在地が一覧にある間）もこの追従は走るが、記録が有効に戻るのは
            // 現在地を削除して上書きするときだけなので、古い値が復活することはない
            _gapIndex = gap - 1;
        return true;
    }

    /// <summary>一覧を空にし、現在地の記録も破棄する。
    /// 一覧だけを空にすると、消えたファイルを指す記録が残る。</summary>
    public void Clear()
    {
        _files.Clear();
        Path = null;
        _gapIndex = null;
    }
}
