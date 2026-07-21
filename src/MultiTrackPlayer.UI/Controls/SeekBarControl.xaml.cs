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

    // クリック直後、マウスがピクセル単位でほぼ動いていなくても WPF は MouseMove を発火する。
    // 閾値なしで毎回 SeekTo すると、1回のクリックでほぼ同時刻に Seeking が2回発火し、
    // エンジン側のシークパイプラインが壊れる（プリロール対象のシリアル対応がズレて再生が固まる）
    // 不具合の引き金になっていた（診断ログで確認済み）。実際のドラッグ移動が閾値を超えた時だけ再発火する
    private const double MinDragPixelDelta = 2.0;
    private double _dragAnchorX;
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
            _markers.Add(new ChapterMarkerData { Ratio = r, X = r * w - 1, ToolTip = t });
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
        double x = e.GetPosition(TrackCanvas).X;
        _dragAnchorX = x;
        SeekTo(x);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        double x = e.GetPosition(TrackCanvas).X;
        if (Math.Abs(x - _dragAnchorX) < MinDragPixelDelta) return;
        _dragAnchorX = x;
        SeekTo(x);
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