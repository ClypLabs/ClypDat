using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClypDat.App.Controls;

// One rendered surface fills every not-yet-restored card slot. This avoids an
// ItemsControl creating hundreds of animated placeholder visual trees while a
// cached library trickles back onto the dispatcher.
internal sealed class LibraryLoadingTilesOverlay : Control
{
    public static readonly StyledProperty<int> TotalTileCountProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, int>(nameof(TotalTileCount));

    public static readonly StyledProperty<int> LoadedTileCountProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, int>(nameof(LoadedTileCount));

    public static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, int>(nameof(ColumnCount), 1);

    public static readonly StyledProperty<double> TileWidthProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(TileWidth));

    public static readonly StyledProperty<double> TileHeightProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(TileHeight));

    public static readonly StyledProperty<double> RowPitchProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(RowPitch));

    public static readonly StyledProperty<double> ScrollOffsetYProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(ScrollOffsetY));

    private const double SweepDurationSeconds = 1.1;
    private const double PauseDurationSeconds = 1.0;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<LibraryLoadingTilesOverlay> ActiveOverlays = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#16202A"));
    private static readonly IBrush ShimmerBrush = CreateShimmerBrush();
    private StreamGeometry? _shimmerGeometry;
    private double _shimmerGeometryHeight;
    private double _shimmerGeometryWidth;

    static LibraryLoadingTilesOverlay()
    {
        AffectsRender<LibraryLoadingTilesOverlay>(
            TotalTileCountProperty,
            LoadedTileCountProperty,
            ColumnCountProperty,
            TileWidthProperty,
            TileHeightProperty,
            RowPitchProperty,
            ScrollOffsetYProperty);
        Timer.Tick += (_, _) =>
        {
            var hasVisibleOverlay = false;
            foreach (var overlay in ActiveOverlays)
            {
                if (!overlay.IsVisible) continue;
                hasVisibleOverlay = true;
                overlay.InvalidateVisual();
            }
            if (!hasVisibleOverlay) Timer.Stop();
        };
    }

    public int TotalTileCount
    {
        get => GetValue(TotalTileCountProperty);
        set => SetValue(TotalTileCountProperty, value);
    }

    public int LoadedTileCount
    {
        get => GetValue(LoadedTileCountProperty);
        set => SetValue(LoadedTileCountProperty, value);
    }

    public int ColumnCount
    {
        get => GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public double TileWidth
    {
        get => GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public double RowPitch
    {
        get => GetValue(RowPitchProperty);
        set => SetValue(RowPitchProperty, value);
    }

    public double ScrollOffsetY
    {
        get => GetValue(ScrollOffsetYProperty);
        set => SetValue(ScrollOffsetYProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActiveOverlays.Add(this);
        if (IsVisible && !Timer.IsEnabled) Timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActiveOverlays.Remove(this);
        if (ActiveOverlays.Count == 0) Timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && !Timer.IsEnabled) Timer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var columns = Math.Max(1, ColumnCount);
        var firstTile = Math.Clamp(LoadedTileCount, 0, TotalTileCount);
        if (firstTile >= TotalTileCount || TileWidth <= 0 || TileHeight <= 0 || RowPitch <= 0) return;

        var viewportTop = Math.Max(0, ScrollOffsetY);
        var viewportBottom = viewportTop + Bounds.Height;
        var firstRow = Math.Max(firstTile / columns, (int)Math.Floor(viewportTop / RowPitch) - 1);
        var lastRow = Math.Min((int)Math.Ceiling(TotalTileCount / (double)columns), (int)Math.Ceiling(viewportBottom / RowPitch) + 1);
        var cycle = SweepDurationSeconds + PauseDurationSeconds;
        var sweep = Math.Min(1, (Clock.Elapsed.TotalSeconds % cycle) / SweepDurationSeconds);
        var tileHeight = Math.Max(TileHeight, RowPitch - 6);
        var shimmerWidth = TileWidth * 1.4;
        EnsureShimmerGeometry(tileHeight, shimmerWidth);

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            for (var row = firstRow; row < lastRow; row++)
            {
                var rowStart = Math.Max(firstTile, row * columns);
                var rowEnd = Math.Min(TotalTileCount, rowStart + columns);
                for (var index = rowStart; index < rowEnd; index++)
                {
                    var column = index % columns;
                    var tile = new Rect(4 + column * (TileWidth + 24), 2 + row * RowPitch - ScrollOffsetY, TileWidth, tileHeight);
                    context.DrawRectangle(TileBrush, null, tile, 12, 12);
                    using (context.PushClip(tile))
                    {
                        var shimmerX = tile.X - shimmerWidth + (TileWidth + shimmerWidth * 2) * sweep;
                        using (context.PushTransform(Matrix.CreateTranslation(shimmerX, tile.Y)))
                        {
                            context.DrawGeometry(ShimmerBrush, null, _shimmerGeometry!);
                        }
                    }
                }
            }
        }
    }

    private void EnsureShimmerGeometry(double tileHeight, double shimmerWidth)
    {
        if (_shimmerGeometry is not null
            && Math.Abs(_shimmerGeometryHeight - tileHeight) < 0.01
            && Math.Abs(_shimmerGeometryWidth - shimmerWidth) < 0.01) return;

        _shimmerGeometryHeight = tileHeight;
        _shimmerGeometryWidth = shimmerWidth;
        var slant = tileHeight * 0.36;
        var shimmer = new StreamGeometry();
        using (var geometry = shimmer.Open())
        {
            geometry.BeginFigure(new Point(-slant, 0), true);
            geometry.LineTo(new Point(shimmerWidth - slant, 0));
            geometry.LineTo(new Point(shimmerWidth + slant, tileHeight));
            geometry.LineTo(new Point(slant, tileHeight));
            geometry.EndFigure(true);
        }
        _shimmerGeometry = shimmer;
    }

    private static IBrush CreateShimmerBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#00344958"), 0),
            new GradientStop(Color.Parse("#0A5A7185"), 0.18),
            new GradientStop(Color.Parse("#1C5A7185"), 0.36),
            new GradientStop(Color.Parse("#365A7185"), 0.5),
            new GradientStop(Color.Parse("#1C5A7185"), 0.64),
            new GradientStop(Color.Parse("#0A5A7185"), 0.82),
            new GradientStop(Color.Parse("#00344958"), 1)
        }
    };
}
