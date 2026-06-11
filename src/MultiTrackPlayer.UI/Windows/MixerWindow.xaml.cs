using System.Windows;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

public partial class MixerWindow : Window
{
    public MixerWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}