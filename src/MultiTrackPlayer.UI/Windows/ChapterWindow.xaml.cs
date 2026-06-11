using System.Windows;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

public partial class ChapterWindow : Window
{
    private readonly MainViewModel _vm;

    public ChapterWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void ChapterList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ChapterList.SelectedItem is ChapterViewModel cvm)
            _vm.Engine.JumpToChapter(cvm.Index);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _vm.ToggleChapterAtCurrentPosition();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChapterViewModel cvm && cvm.IsUserDefined)
        {
            _vm.Engine.RemoveUserChapter(cvm.Chapter);
            _vm.RefreshChapters();
        }
    }
}