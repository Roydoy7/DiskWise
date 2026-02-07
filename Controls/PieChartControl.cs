using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DiskWise.Controls;

public class PieSliceData
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public double Percentage { get; set; }
    public string SizeDisplay { get; set; } = string.Empty;
    public Brush Fill { get; set; } = Brushes.Gray;
    public object? Tag { get; set; }
}

public class PieChartControl : Canvas
{
    private static readonly Brush[] SliceColors =
    [
        new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), // Cyan
        new SolidColorBrush(Color.FromRgb(0xB2, 0x4B, 0xF3)), // Purple
        new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x78)), // Pink
        new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88)), // Green
        new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)), // Yellow
        new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00)), // Orange
        new SolidColorBrush(Color.FromRgb(0x3D, 0x9B, 0xFF)), // Blue
        new SolidColorBrush(Color.FromRgb(0xE0, 0x66, 0xFF)), // Light purple
    ];

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<PieSliceData>),
            typeof(PieChartControl), new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable<PieSliceData>? ItemsSource
    {
        get => (IEnumerable<PieSliceData>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly RoutedEvent SliceClickedEvent =
        EventManager.RegisterRoutedEvent("SliceClicked", RoutingStrategy.Bubble,
            typeof(RoutedPropertyChangedEventHandler<PieSliceData?>), typeof(PieChartControl));

    public event RoutedPropertyChangedEventHandler<PieSliceData?> SliceClicked
    {
        add => AddHandler(SliceClickedEvent, value);
        remove => RemoveHandler(SliceClickedEvent, value);
    }

    private PieSliceData? _hoveredSlice;

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PieChartControl chart)
            chart.Rebuild();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var items = ItemsSource?.ToList();
        if (items == null || items.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var size = Math.Min(ActualWidth, ActualHeight);
        var cx = ActualWidth / 2;
        var cy = size / 2;
        var radius = size / 2 - 4; // small margin for glow
        var innerRadius = radius * 0.45; // donut hole

        double startAngle = -90; // start from top
        var total = items.Sum(i => i.Value);
        if (total <= 0) return;

        // Assign colors
        for (int i = 0; i < items.Count; i++)
            items[i].Fill = SliceColors[i % SliceColors.Length];

        for (int i = 0; i < items.Count; i++)
        {
            var slice = items[i];
            var sweepAngle = slice.Value / total * 360;
            if (sweepAngle < 0.5) continue; // skip tiny slices

            var path = CreateSlicePath(cx, cy, radius, innerRadius, startAngle, sweepAngle, slice);
            Children.Add(path);
            startAngle += sweepAngle;
        }

        // Center text
        var centerText = new TextBlock
        {
            Text = items.Count.ToString(),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4)),
            TextAlignment = TextAlignment.Center
        };
        centerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(centerText, cx - centerText.DesiredSize.Width / 2);
        SetTop(centerText, cy - centerText.DesiredSize.Height / 2 - 6);
        Children.Add(centerText);

        var subText = new TextBlock
        {
            Text = "items",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7B, 0x84, 0x98)),
            TextAlignment = TextAlignment.Center
        };
        subText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(subText, cx - subText.DesiredSize.Width / 2);
        SetTop(subText, cy + centerText.DesiredSize.Height / 2 - 8);
        Children.Add(subText);
    }

    private Path CreateSlicePath(double cx, double cy, double outerR, double innerR,
        double startAngle, double sweepAngle, PieSliceData slice)
    {
        var startRad = startAngle * Math.PI / 180;
        var endRad = (startAngle + sweepAngle) * Math.PI / 180;
        var isLarge = sweepAngle > 180;

        var outerStart = new Point(cx + outerR * Math.Cos(startRad), cy + outerR * Math.Sin(startRad));
        var outerEnd = new Point(cx + outerR * Math.Cos(endRad), cy + outerR * Math.Sin(endRad));
        var innerStart = new Point(cx + innerR * Math.Cos(endRad), cy + innerR * Math.Sin(endRad));
        var innerEnd = new Point(cx + innerR * Math.Cos(startRad), cy + innerR * Math.Sin(startRad));

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(outerR, outerR), 0, isLarge,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerStart, true));
        figure.Segments.Add(new ArcSegment(innerEnd, new Size(innerR, innerR), 0, isLarge,
            SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var fillBrush = slice.Fill.Clone();
        fillBrush.Opacity = 0.75;

        var path = new Path
        {
            Data = geometry,
            Fill = fillBrush,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x08, 0x0B, 0x14)),
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            Tag = slice,
            ToolTip = $"{slice.Name}\n{slice.SizeDisplay} ({slice.Percentage:F1}%)"
        };

        path.MouseEnter += (s, e) =>
        {
            if (s is Path p)
            {
                var b = slice.Fill.Clone();
                b.Opacity = 1.0;
                p.Fill = b;
                p.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = ((SolidColorBrush)slice.Fill).Color,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.5
                };
                _hoveredSlice = slice;
            }
        };

        path.MouseLeave += (s, e) =>
        {
            if (s is Path p)
            {
                var b = slice.Fill.Clone();
                b.Opacity = 0.75;
                p.Fill = b;
                p.Effect = null;
                _hoveredSlice = null;
            }
        };

        path.MouseLeftButtonDown += (s, e) =>
        {
            if (s is Path p && p.Tag is PieSliceData data)
            {
                RaiseEvent(new RoutedPropertyChangedEventArgs<PieSliceData?>(null, data, SliceClickedEvent));
                e.Handled = true;
            }
        };

        return path;
    }
}
