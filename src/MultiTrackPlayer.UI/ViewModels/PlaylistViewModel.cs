using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class PlaylistViewModel : ObservableObject
{
    public ObservableCollection<string> Files { get; } = new();

    [ObservableProperty] private int _currentIndex = -1;

    public string? CurrentFile => CurrentIndex >= 0 && CurrentIndex < Files.Count ? Files[CurrentIndex] : null;

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!Files.Contains(p))
                Files.Add(p);
    }

    public void Remove(string path) => Files.Remove(path);

    /// <summary>
    /// 次のファイルへ進む。現在位置が未確定（-1）の場合は先頭のファイルを返す
    /// （プレイリストから再生を始める操作のため）。
    /// 再生終了後の自動送りでは、位置が未確定のまま呼ぶと再生し終えたファイル自身が
    /// 返りうるので、呼び出し側で CurrentIndex を確認すること。
    /// </summary>
    public string? MoveNext()
    {
        if (Files.Count == 0 || CurrentIndex >= Files.Count - 1) return null;
        CurrentIndex++;
        return Files[CurrentIndex];
    }

    public string? MovePrevious()
    {
        if (Files.Count == 0 || CurrentIndex <= 0) return null;
        CurrentIndex--;
        return Files[CurrentIndex];
    }

    /// <summary>
    /// 再生中のファイルに対応する位置へ現在位置を合わせる。
    /// プレイリストに無いファイルなら -1（位置は未確定）にする。見つからないときに
    /// 前の値を残すと、次送りや自動送りが無関係な位置を基準に動いてしまう。
    /// </summary>
    public void SetCurrentByPath(string path) => CurrentIndex = Files.IndexOf(path);
}