using CommunityToolkit.Mvvm.ComponentModel;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class ChapterViewModel : ObservableObject
{
    public ChapterInfo Chapter { get; }

    public int Index => Chapter.Index;
    public string TimeLabel => Chapter.StartTime.ToString(@"hh\:mm\:ss");
    public bool IsUserDefined => Chapter.IsUserDefined;

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isEditing;

    public ChapterViewModel(ChapterInfo chapter)
    {
        Chapter = chapter;
        _title = chapter.Title;
    }
}