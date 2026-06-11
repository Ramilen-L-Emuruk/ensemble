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

    public string? MoveNext()
    {
        if (Files.Count == 0) return null;
        CurrentIndex = (CurrentIndex + 1) % Files.Count;
        return Files[CurrentIndex];
    }

    public string? MovePrevious()
    {
        if (Files.Count == 0) return null;
        CurrentIndex = CurrentIndex <= 0 ? Files.Count - 1 : CurrentIndex - 1;
        return Files[CurrentIndex];
    }

    public void SetCurrentByPath(string path)
    {
        int idx = Files.IndexOf(path);
        if (idx >= 0) CurrentIndex = idx;
    }
}