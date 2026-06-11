using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MultiTrackPlayer.UI.Controls;

public partial class SeekBarControl : UserControl
{
    public static readonly DependencyProperty PositionRatioProperty =
        DependencyProperty.Register(nameof(PositionRatio), typeof(double), typeof(SeekBarControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(SeekBarControl),
            new PropertyMetadata(TimeSpan.Zero));

    public double PositionRatio
    {
        get => (double)GetValue(PositionRatioProperty);
        set => SetValue(PositionRatioProperty, value);
    }

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public event EventHandler<double>? Seeking;

    private bool _isDragging;
    private readonly ObservableCollection<ChapterMarkerData> _markers = new();

    public SeekBarControl()
    {
        InitializeComponent();
        ChapterMarkers.ItemsSource = _markers;
        SizeChanged += (_, _) => UpdateVisuals();
    }

    private static void OnRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SeekBarControl ctrl && !ctrl._isDragging)
            ctrl.UpdateVisuals();
    }

    public void SetChapters(IEnumerable<(double ratio, string title)> chapters)
    {
        _markers.Clear();
        double w = TrackCanvas.ActualWidth;
        foreach (var (r, t) in chapters)
            _markers.Add(new ChapterMarkerData { X = r * w - 1, ToolTip = t });
    }

    private void UpdateVisuals()
    {
        double w = TrackCanvas.ActualWidth;
        if (w <= 0) return;
        double x = PositionRatio * w;
        ProgressRect.Width = Math.Max(0, x);
        System.Windows.Controls.Canvas.SetLeft(Thumb, x - 6);

        foreach (var m in _markers)
            m.X = m.Ratio * w - 1;
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        TrackCanvas.CaptureMouse();
        SeekTo(e.GetPosition(TrackCanvas).X);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        SeekTo(e.GetPosition(TrackCanvas).X);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        TrackCanvas.ReleaseMouseCapture();
    }

    private void SeekTo(double x)
    {
        double w = TrackCanvas.ActualWidth;
        if (w <= 0) return;
        double ratio = Math.Clamp(x / w, 0, 1);
        PositionRatio = ratio;
        Seeking?.Invoke(this, ratio);
        UpdateVisuals();
    }
}

public class ChapterMarkerData : System.ComponentModel.INotifyPropertyChanged
{
    private double _x;
    public double Ratio { get; set; }
    public double X { get => _x; set { _x = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(X))); } }
    public string ToolTip { get; set; } = string.Empty;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}