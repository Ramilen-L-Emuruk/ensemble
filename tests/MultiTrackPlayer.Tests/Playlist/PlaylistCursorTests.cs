using MultiTrackPlayer.Core.Models;
using Xunit;

namespace MultiTrackPlayer.Tests.Playlist;

/// <summary>
/// <see cref="PlaylistCursor"/> の位置決めを検証する。
/// 「現在地より前を削除すると自動送りが 1 本飛ぶ」「削除したファイルを再追加すると
/// 削除当時の位置を返し続ける」系の回帰を押さえる。
/// </summary>
public sealed class PlaylistCursorTests
{
    private static (List<string> Files, PlaylistCursor Cursor) Create(params string[] files)
    {
        var list = new List<string>(files);
        return (list, new PlaylistCursor(list));
    }

    [Fact(DisplayName = "起点が未確定なら次送りは先頭のファイルを返す")]
    public void PeekNext_ReturnsFirstFile_WhenOriginIsUnknown()
    {
        var (_, cursor) = Create("A", "B", "C");

        Assert.False(cursor.HasAdvanceOrigin);
        Assert.Equal("A", cursor.PeekNext());
        Assert.Null(cursor.PeekPrevious());
    }

    [Fact(DisplayName = "現在地の前後のファイルを返す")]
    public void PeekNextAndPrevious_ReturnNeighbours_WhenCursorIsInList()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("B");

        Assert.True(cursor.HasAdvanceOrigin);
        Assert.Equal(1, cursor.Index);
        Assert.Equal("C", cursor.PeekNext());
        Assert.Equal("A", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "一覧の端では次・前のファイルが無い")]
    public void PeekNextAndPrevious_ReturnNull_AtBothEnds()
    {
        var (_, cursor) = Create("A", "B");

        cursor.MoveTo("B");
        Assert.Null(cursor.PeekNext());

        cursor.MoveTo("A");
        Assert.Null(cursor.PeekPrevious());
    }

    [Fact(DisplayName = "覗き見るだけでは現在地が動かない")]
    public void PeekNext_DoesNotMoveCursor()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("A");

        Assert.Equal("B", cursor.PeekNext());
        Assert.Equal("B", cursor.PeekNext());
        Assert.Equal("A", cursor.Path);
    }

    [Fact(DisplayName = "一覧に無いファイルを現在地にしても起点は未確定のまま")]
    public void HasAdvanceOrigin_IsFalse_WhenCursorFileIsNotInList()
    {
        var (_, cursor) = Create("A", "B");
        cursor.MoveTo(@"C:\他所\D.mp4");

        Assert.Equal(-1, cursor.Index);
        Assert.False(cursor.HasAdvanceOrigin);
        // 手動の次送りは先頭から始められる（自動送りは HasAdvanceOrigin で止める）
        Assert.Equal("A", cursor.PeekNext());
    }

    [Fact(DisplayName = "現在地より前を削除しても次送りは正しい後続を返す")]
    public void Remove_KeepsNeighbours_WhenRemovedBeforeCursor()
    {
        var (files, cursor) = Create("A", "B", "C", "D");
        cursor.MoveTo("C");

        Assert.True(cursor.Remove("A"));

        Assert.Equal(new[] { "B", "C", "D" }, files);
        Assert.Equal(1, cursor.Index);
        Assert.Equal("D", cursor.PeekNext());
        Assert.Equal("B", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "現在地より後を削除しても現在地は動かない")]
    public void Remove_KeepsCursor_WhenRemovedAfterCursor()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("A");

        cursor.Remove("C");

        Assert.Equal(0, cursor.Index);
        Assert.Equal("B", cursor.PeekNext());
    }

    [Fact(DisplayName = "現在地を削除すると繰り上がった後続が次になる")]
    public void Remove_MakesFollowerNext_WhenCursorFileIsRemoved()
    {
        var (_, cursor) = Create("A", "B", "C", "D");
        cursor.MoveTo("C");

        cursor.Remove("C");

        // 現在地は一覧から外れるが、次送りの起点は空いた位置として残る
        Assert.Equal(-1, cursor.Index);
        Assert.True(cursor.HasAdvanceOrigin);
        Assert.Equal("D", cursor.PeekNext());
        Assert.Equal("B", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "先頭にある現在地を削除すると繰り上がった先頭が次になる")]
    public void Remove_MakesNewFirstFileNext_WhenCursorWasFirst()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("A");

        cursor.Remove("A");

        Assert.True(cursor.HasAdvanceOrigin);
        Assert.Equal("B", cursor.PeekNext());
        Assert.Null(cursor.PeekPrevious());
    }

    [Fact(DisplayName = "末尾にある現在地を削除すると次のファイルは無い")]
    public void PeekNext_ReturnsNull_WhenCursorWasLastAndRemoved()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("C");

        cursor.Remove("C");

        Assert.Null(cursor.PeekNext());
        Assert.Equal("B", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "現在地を削除した後にその前を削除しても空いた位置が追従する")]
    public void Remove_ShiftsGap_WhenRemovedBeforeGap()
    {
        var (files, cursor) = Create("A", "B", "C", "D");
        cursor.MoveTo("C");

        cursor.Remove("C");
        // 空いた位置（元 C の位置）より前を削除する
        cursor.Remove("A");

        Assert.Equal(new[] { "B", "D" }, files);
        Assert.Equal("D", cursor.PeekNext());
        Assert.Equal("B", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "現在地を削除した後に次送り先を削除するとその後続が次になる")]
    public void Remove_KeepsGap_WhenRemovedAtGap()
    {
        var (files, cursor) = Create("A", "B", "C", "D");
        cursor.MoveTo("B");

        cursor.Remove("B");
        // 空いた位置にいる C（次送り先）自体を削除すると、D が同じ位置へ繰り上がる
        cursor.Remove("C");

        Assert.Equal(new[] { "A", "D" }, files);
        Assert.Equal("D", cursor.PeekNext());
        Assert.Equal("A", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "削除した現在地と同じパスを再追加すると空いた位置の記録は無効になる")]
    public void Gap_IsIgnored_WhenCursorFileIsAddedBack()
    {
        var (files, cursor) = Create("A", "B", "C", "D");
        cursor.MoveTo("B");
        cursor.Remove("B");

        files.Add("B");

        Assert.Equal(new[] { "A", "C", "D", "B" }, files);
        Assert.Equal(3, cursor.Index);
        // 末尾へ戻ったので次は無く、前は D
        Assert.Null(cursor.PeekNext());
        Assert.Equal("D", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "現在地を移し直すと空いた位置の記録は捨てられる")]
    public void MoveTo_ClearsGap()
    {
        var (_, cursor) = Create("A", "B", "C");
        cursor.MoveTo("A");
        cursor.Remove("A");

        cursor.MoveTo("C");

        Assert.Equal("C", cursor.Path);
        Assert.Null(cursor.PeekNext());
        Assert.Equal("B", cursor.PeekPrevious());
    }

    [Fact(DisplayName = "一覧に無いパスの削除は何もしない")]
    public void Remove_ReturnsFalse_WhenPathIsNotInList()
    {
        var (files, cursor) = Create("A", "B");
        cursor.MoveTo("A");

        Assert.False(cursor.Remove("Z"));

        Assert.Equal(new[] { "A", "B" }, files);
        Assert.Equal(0, cursor.Index);
    }

    [Fact(DisplayName = "クリアで一覧と現在地の記録が消える")]
    public void Clear_ResetsListAndCursor()
    {
        var (files, cursor) = Create("A", "B", "C");
        cursor.MoveTo("B");

        cursor.Clear();

        Assert.Empty(files);
        Assert.Null(cursor.Path);
        Assert.Equal(-1, cursor.Index);
        Assert.False(cursor.HasAdvanceOrigin);
        Assert.Null(cursor.PeekNext());
    }

    [Fact(DisplayName = "現在地を削除してからクリアしても空いた位置の記録は残らない")]
    public void Clear_DiscardsGap()
    {
        var (files, cursor) = Create("A", "B", "C");
        cursor.MoveTo("B");
        cursor.Remove("B");

        cursor.Clear();
        files.Add("X");

        Assert.False(cursor.HasAdvanceOrigin);
        Assert.Equal("X", cursor.PeekNext());
    }

    [Fact(DisplayName = "空の一覧では次・前のファイルが無い")]
    public void PeekNextAndPrevious_ReturnNull_WhenListIsEmpty()
    {
        var (_, cursor) = Create();

        Assert.Null(cursor.PeekNext());
        Assert.Null(cursor.PeekPrevious());
        Assert.False(cursor.HasAdvanceOrigin);
    }
}
