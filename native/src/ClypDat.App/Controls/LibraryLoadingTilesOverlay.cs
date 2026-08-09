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

    private const double SweepDurationSeconds = 0.75;
    private const double PauseDurationSeconds = 1.0;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<LibraryLoadingTilesOverlay> ActiveOverlays = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#16202A"));
    private static readonly IBrush ShimmerBrush = CreateShimmerBrush();
    private Rect _effectiveViewport;

    static LibraryLoadingTilesOverlay()
    {
        AffectsRender<LibraryLoadingTilesOverlay>(
            TotalTileCountProperty,
            LoadedTileCountProperty,
            ColumnCountProperty,
            TileWidthProperty,
            TileHeightProperty,
            RowPitchProperty);
        AffectsMeasure<LibraryLoadingTilesOverlay>(
            TotalTileCountProperty,
            ColumnCountProperty,
            RowPitchProperty);
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

    public LibraryLoadingTilesOverlay()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _effectiveViewport = e.EffectiveViewport;
            InvalidateVisual();
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

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = (int)Math.Ceiling(Math.Max(0, TotalTileCount) / (double)Math.Max(1, ColumnCount));
        return new Size(0, Math.Max(0, rows * RowPitch));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var columns = Math.Max(1, ColumnCount);
        var firstTile = Math.Clamp(LoadedTileCount, 0, TotalTileCount);
        if (firstTile >= TotalTileCount || TileWidth <= 0 || TileHeight <= 0 || RowPitch <= 0) return;

        var viewport = _effectiveViewport.Width > 0 && _effectiveViewport.Height > 0
            ? _effectiveViewport
            : new Rect(Bounds.Size);
        var firstRow = Math.Max(firstTile / columns, (int)Math.Floor(viewport.Y / RowPitch) - 1);
        var lastRow = Math.Min((int)Math.Ceiling(TotalTileCount / (double)columns), (int)Math.Ceiling(viewport.Bottom / RowPitch) + 1);
        var cycle = SweepDurationSeconds + PauseDurationSeconds;
        var sweep = Math.Min(1, (Clock.Elapsed.TotalSeconds % cycle) / SweepDurationSeconds);
        var shimmerWidth = TileWidth * 0.58;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            for (var row = firstRow; row < lastRow; row++)
            {
                var rowStart = Math.Max(firstTile, row * columns);
                var rowEnd = Math.Min(TotalTileCount, rowStart + columns);
                for (var index = rowStart; index < rowEnd; index++)
                {
                    var column = index % columns;
                    var tile = new Rect(4 + column * (TileWidth + 24), 2 + row * RowPitch, TileWidth, TileHeight);
                    context.DrawRectangle(TileBrush, null, tile, 12, 12);
                    using (context.PushClip(tile))
                    {
                        var shimmerX = tile.X - shimmerWidth + (TileWidth + shimmerWidth * 2) * sweep;
                        var slant = tile.Height * 0.36;
                        var shimmer = new StreamGeometry();
                        using (var geometry = shimmer.Open())
                        {
                            geometry.BeginFigure(new Point(shimmerX - slant, tile.Y), true);
                            geometry.LineTo(new Point(shimmerX + shimmerWidth - slant, tile.Y));
                            geometry.LineTo(new Point(shimmerX + shimmerWidth + slant, tile.Bottom));
                            geometry.LineTo(new Point(shimmerX + slant, tile.Bottom));
                            geometry.EndFigure(true);
                        }
                        context.DrawGeometry(ShimmerBrush, null, shimmer);
                    }
                }
            }
        }
    }

    private static IBrush CreateShimmerBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#00344958"), 0),
            new GradientStop(Color.Parse("#245A7185"), 0.28),
            new GradientStop(Color.Parse("#805A7185"), 0.5),
            new GradientStop(Color.Parse("#245A7185"), 0.72),
            new GradientStop(Color.Parse("#00344958"), 1)
        }
    };
}
