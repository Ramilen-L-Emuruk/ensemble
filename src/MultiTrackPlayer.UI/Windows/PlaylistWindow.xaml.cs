using System.Windows;
using Microsoft.Win32;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

public partial class PlaylistWindow : Window
{
    private readonly MainViewModel _vm;

    public PlaylistWindow(MainViewModel vm)
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

    private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is string path)
        {
            _vm.Playlist.SetCurrentByPath(path);
            _vm.OpenFile(path);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "動画ファイル|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.webm;*.flv|すべてのファイル|*.*" };
        if (dlg.ShowDialog() == true)
            _vm.Playlist.AddFiles(dlg.FileNames);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is string path)
            _vm.Playlist.Remove(path);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _vm.Playlist.Files.Clear();

    private void Window_DragOver(object sender, DragEventArgs e)
        => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _vm.Playlist.AddFiles(files);
    }
}