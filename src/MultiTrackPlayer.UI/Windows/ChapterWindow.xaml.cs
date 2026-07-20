using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void ChapterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChapterList.SelectedItem is ChapterViewModel cvm)
            _vm.Engine.JumpToChapter(cvm.Index);
    }

    private void ChapterList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ChapterList.SelectedItem is not ChapterViewModel cvm) return;

        switch (e.Key)
        {
            case Key.Delete:
                if (cvm.IsUserDefined)
                {
                    _vm.Engine.RemoveUserChapter(cvm.Chapter);
                    _vm.RefreshChapters();
                }
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.Engine.JumpToChapter(cvm.Index);
                e.Handled = true;
                break;
            case Key.F2:
                if (cvm.IsUserDefined) cvm.IsEditing = true;
                e.Handled = true;
                break;
        }
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

    // ── タイトルのインライン編集 ──

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return; // シングルクリックは通常の選択処理に任せる

        if ((sender as FrameworkElement)?.DataContext is ChapterViewModel cvm && cvm.IsUserDefined)
            cvm.IsEditing = true;
        e.Handled = true; // 行全体のダブルクリック(ジャンプ)への伝播を止める
    }

    private void TitleEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChapterViewModel cvm) return;

        if (e.Key == Key.Enter)
        {
            cvm.IsEditing = false;
            _vm.RenameChapter(cvm, tb.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            cvm.IsEditing = false;
            e.Handled = true;
        }
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChapterViewModel cvm) return;
        if (!cvm.IsEditing) return;

        cvm.IsEditing = false;
        _vm.RenameChapter(cvm, tb.Text);
    }
}
