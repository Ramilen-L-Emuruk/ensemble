using System.Windows;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Windows;

public partial class DebugWindow : Window
{
    public DebugWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SubWindowKeyHandling.AttachEscapeAndShortcutForwarding(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
