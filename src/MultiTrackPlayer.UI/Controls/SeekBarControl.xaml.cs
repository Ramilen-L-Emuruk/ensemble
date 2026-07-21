using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.Controls;

public partial class SeekBarControl : UserControl
{
    public static readonly DependencyProperty PositionRatioProperty =
        DependencyProperty.Register(nameof(PositionRatio), typeof(double), typeof(SeekBarControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(SeekBarControl),
            new PropertyMetadata(TimeSpan.Zero));

    public static readonly DependencyProperty ThumbnailSheetProperty =
        DependencyProperty.Register(nameof(ThumbnailSheet), typeof(ThumbnailSheet), typeof(SeekBarControl),
            new PropertyMetadata(null));

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

    public ThumbnailSheet? ThumbnailSheet
    {
        get => (ThumbnailSheet?)GetValue(ThumbnailSheetProperty);
        set => SetValue(ThumbnailSheetProperty, value);
    }

    public event EventHandler<double>? Seeking;

    // クリック直後、マウスがピクセル単位でほぼ動いていなくても WPF は MouseMove を発火する。
    // 閾値なしで毎回 SeekTo すると、1回のクリックでほぼ同時刻に Seeking が2回発火し、
    // エンジン側のシークパイプラインが壊れる（プリロール対象のシリアル対応がズレて再生が固まる）
    // 不具合の引き金になっていた（診断ログで確認済み）。実際のドラッグ移動が閾値を超えた時だけ再発火する
    private const double MinDragPixelDelta = 2.0;
    // ホバー直後に毎回プレビューを出すと、素通りしただけでも一瞬チラつくため表示までに遅延を入れる。
    // 一度表示された後の追従はこの遅延なしで即座に更新する（キャッシュ参照だけなので重くない）
    private static readonly TimeSpan HoverShowDelay = TimeSpan.FromMilliseconds(150);

    private double _dragAnchorX;
    private bool _isDragging;
    private readonly ObservableCollection<ChapterMarkerData> _markers = new();

    private readonly DispatcherTimer _hoverShowTimer;
    private double _pendingHoverX;
    private bool _isPopupShown;
    private BitmapImage? _sheetBitmap;
    private string? _loadedSheetPath;
    private int _loadedSheetVersion = -1;

    public SeekBarControl()
    {
        InitializeComponent();
        ChapterMarkers.ItemsSource = _markers;
        SizeChanged += (_, _) => UpdateVisuals();

        _hoverShowTimer = new DispatcherTimer { Interval = HoverShowDelay };
        _hoverShowTimer.Tick += (_, _) =>
        {
            _hoverShowTimer.Stop();
            _isPopupShown = true;
            ShowOrUpdateThumbnail(_pendingHoverX);
        };
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
        double x = e.GetPosition(TrackCanvas).X;

        if (_isPopupShown)
            ShowOrUpdateThumbnail(x);
        else
            _pendingHoverX = x;

        if (!_isDragging) return;
        if (Math.Abs(x - _dragAnchorX) < MinDragPixelDelta) return;
        _dragAnchorX = x;
        SeekTo(x);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        TrackCanvas.ReleaseMouseCapture();
    }

    private void Canvas_MouseEnter(object sender, MouseEventArgs e)
    {
        _pendingHoverX = e.GetPosition(TrackCanvas).X;
        _isPopupShown = false;
        _hoverShowTimer.Stop();
        _hoverShowTimer.Start();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverShowTimer.Stop();
        _isPopupShown = false;
        ThumbnailPopup.IsOpen = false;
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

    private void ShowOrUpdateThumbnail(double canvasX)
    {
        var sheet = ThumbnailSheet;
        double w = TrackCanvas.ActualWidth;
        if (sheet == null || Duration <= TimeSpan.Zero || w <= 0)
        {
            ThumbnailPopup.IsOpen = false;
            return;
        }

        EnsureSheetBitmapLoaded(sheet);
        if (_sheetBitmap == null)
        {
            ThumbnailPopup.IsOpen = false;
            return;
        }

        double ratio = Math.Clamp(canvasX / w, 0, 1);
        var position = TimeSpan.FromSeconds(ratio * Duration.TotalSeconds);
        var (tileX, tileY, tileWidth, tileHeight) = sheet.GetTileRect(position);

        // ビットマップの実ピクセルサイズを超えないようクランプ（末尾タイルの端数保険）
        tileX = Math.Clamp(tileX, 0, Math.Max(0, _sheetBitmap.PixelWidth - tileWidth));
        tileY = Math.Clamp(tileY, 0, Math.Max(0, _sheetBitmap.PixelHeight - tileHeight));

        ThumbnailImage.Source = new CroppedBitmap(_sheetBitmap, new Int32Rect(tileX, tileY, tileWidth, tileHeight));
        ThumbnailTimeText.Text = position.ToString(@"hh\:mm\:ss");

        ThumbnailPopup.HorizontalOffset = canvasX - tileWidth / 2.0;
        ThumbnailPopup.VerticalOffset = -(tileHeight + 28);
        ThumbnailPopup.IsOpen = true;
    }

    /// <summary>
    /// スプライトシートのJPEGを読み込んで保持する。Uri経由だとWPFの内部キャッシュにより
    /// 同一パスのファイルが後から再生成されても古い内容が返る恐れがあるため、必ずStream経由で読む。
    /// 生成中は同じパスのファイルが Version を上げながら上書きされていくため、パスだけでなく
    /// Version も見て変化を検知する
    /// </summary>
    private void EnsureSheetBitmapLoaded(ThumbnailSheet sheet)
    {
        if (_loadedSheetPath == sheet.SheetPath && _loadedSheetVersion == sheet.Version && _sheetBitmap != null) return;

        try
        {
            using var stream = new FileStream(sheet.SheetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            _sheetBitmap = bitmap;
            _loadedSheetPath = sheet.SheetPath;
            _loadedSheetVersion = sheet.Version;
        }
        catch
        {
            _sheetBitmap = null;
            _loadedSheetPath = null;
            _loadedSheetVersion = -1;
        }
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
