using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.ViewModels;

/// <summary>
/// プレイリストの一覧と、UI の選択位置を持つ。位置決めのロジックは
/// <see cref="PlaylistCursor"/> に委ねる（FFmpeg・WPF に依存しないためテストできる）。
/// 「一覧の選択」（<see cref="SelectedIndex"/>）と「プレイリスト上の現在地」
/// （<see cref="CursorPath"/>）は別々に持つ。ひとつの状態を共有させると、一覧の行を
/// クリックしただけで次に再生するファイルが書き換わる。
/// </summary>
/// <remarks>
/// <see cref="PlaylistCursor"/> へ委譲しているプロパティ（<see cref="CursorPath"/>・
/// <see cref="CursorIndex"/>・<see cref="HasAdvanceOrigin"/>）は変更通知を出さない。
/// バインドしても更新されないので、表示に使うならそのとき通知を足すこと。
/// </remarks>
public partial class PlaylistViewModel : ObservableObject
{
    private readonly ObservableCollection<string> _files = new();
    private readonly PlaylistCursor _cursor;

    public PlaylistViewModel()
    {
        _cursor = new PlaylistCursor(_files);
        Files = new ReadOnlyObservableCollection<string>(_files);
    }

    /// <summary>
    /// プレイリストの一覧（表示用）。変更は <see cref="AddFiles"/>・<see cref="Remove"/>・
    /// <see cref="Clear"/> だけを通す。直接書き換えられると現在地の記録が付いてこず、
    /// 「削除すると自動送りが 1 本飛ぶ」類の不具合に戻る。
    /// </summary>
    public ReadOnlyObservableCollection<string> Files { get; }

    /// <summary>一覧で選択されている位置。ユーザーの操作でも直接変わるが、
    /// 現在地には影響しない（追従は現在地 → 選択の一方向だけ）。</summary>
    [ObservableProperty] private int _selectedIndex = -1;

    /// <inheritdoc cref="PlaylistCursor.Path"/>
    public string? CursorPath => _cursor.Path;

    /// <inheritdoc cref="PlaylistCursor.Index"/>
    public int CursorIndex => _cursor.Index;

    /// <inheritdoc cref="PlaylistCursor.HasAdvanceOrigin"/>
    public bool HasAdvanceOrigin => _cursor.HasAdvanceOrigin;

    /// <summary>一覧の末尾へ追加する（同じパスは二重に入れない）。</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_files.Contains(p))
                _files.Add(p);
    }

    /// <summary>一覧から 1 件削除する。一覧に無いパスなら何もしない。
    /// 現在地は <see cref="PlaylistCursor"/> が保つ。</summary>
    public void Remove(string path) => _cursor.Remove(path);

    /// <inheritdoc cref="PlaylistCursor.Clear"/>
    /// <remarks>一覧の選択（<see cref="SelectedIndex"/>）も解除する。</remarks>
    public void Clear()
    {
        _cursor.Clear();
        SelectedIndex = -1;
    }

    /// <inheritdoc cref="PlaylistCursor.PeekNext"/>
    public string? PeekNext() => _cursor.PeekNext();

    /// <inheritdoc cref="PlaylistCursor.PeekPrevious"/>
    public string? PeekPrevious() => _cursor.PeekPrevious();

    /// <summary>
    /// 開こうとしたファイルへ現在地を移し、一覧の選択もその行へ合わせる。
    /// 選択の追従をここで行うのは、呼び出し側に覚えさせるとファイルを開く経路が
    /// 増えたときに漏れるため。
    /// </summary>
    public void SetCursor(string path)
    {
        _cursor.MoveTo(path);
        SelectedIndex = CursorIndex;
    }
}
