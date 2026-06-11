using CommunityToolkit.Mvvm.ComponentModel;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class ChapterViewModel : ObservableObject
{
    public ChapterInfo Chapter { get; }

    public int Index => Chapter.Index;
    public string Title => Chapter.Title;
    public string TimeLabel => Chapter.StartTime.ToString(@"hh\:mm\:ss");
    public bool IsUserDefined => Chapter.IsUserDefined;

    public ChapterViewModel(ChapterInfo chapter)
    {
        Chapter = chapter;
    }
}