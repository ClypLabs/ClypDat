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

    public static readonly StyledProperty<double> TileImageHeightProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(TileImageHeight));

    public static readonly StyledProperty<double> TileTopInsetProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(TileTopInset));

    public static readonly StyledProperty<double> RowPitchProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(RowPitch));

    public static readonly StyledProperty<double> ScrollOffsetYProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(ScrollOffsetY));

    private const double SweepDurationSeconds = 1.25;
    private const double PauseDurationSeconds = 0.75;
    // Each tile starts its sweep slightly after the one up-left of it, so the
    // highlight reads as a single diagonal wave crossing the grid.
    private const double DiagonalStaggerSeconds = 0.085;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<LibraryLoadingTilesOverlay> ActiveOverlays = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private static readonly IBrush SurfaceBrush = CreateSurfaceBrush();
    private static readonly IBrush ThumbBrush = CreateThumbBrush();
    private static readonly IPen SurfacePen = new Pen(new SolidColorBrush(Color.Parse("#1AFFFFFF")), 1);
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#12FFFFFF"));
    private static readonly IBrush GlyphBrush = new SolidColorBrush(Color.Parse("#0EFFFFFF"));
    private static readonly IBrush ShimmerBrush = CreateShimmerBrush();
    private static readonly BoxShadows TileShadow = new(new BoxShadow
    {
        OffsetX = 0,
        OffsetY = 6,
        Blur = 18,
        Spread = -6,
        Color = Color.Parse("#66000000")
    });
    private StreamGeometry? _shimmerGeometry;
    private double _shimmerGeometryHeight;
    private double _shimmerGeometryWidth;
    private RectangleGeometry? _clipGeometry;
    private StreamGeometry? _glyphGeometry;
    private double _glyphGeometrySize;

    static LibraryLoadingTilesOverlay()
    {
        AffectsRender<LibraryLoadingTilesOverlay>(
            TotalTileCountProperty,
            LoadedTileCountProperty,
            ColumnCountProperty,
            TileWidthProperty,
            TileHeightProperty,
            TileImageHeightProperty,
            TileTopInsetProperty,
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

    public double TileImageHeight
    {
        get => GetValue(TileImageHeightProperty);
        set => SetValue(TileImageHeightProperty, value);
    }

    public double TileTopInset
    {
        get => GetValue(TileTopInsetProperty);
        set => SetValue(TileTopInsetProperty, value);
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
        var now = Clock.Elapsed.TotalSeconds;
        var tileHeight = TileHeight;
        var tileWidth = TileWidth;
        var imageHeight = TileImageHeight > 0
            ? Math.Clamp(TileImageHeight, 0, tileHeight)
            : tileHeight * 0.72;
        var footerHeight = tileHeight - imageHeight;
        var shimmerWidth = tileWidth * 1.15;
        EnsureShimmerGeometry(tileHeight, shimmerWidth);
        var glyphSize = Math.Min(imageHeight, tileWidth) * 0.34;
        EnsureTileGeometry(tileWidth, tileHeight, glyphSize);
        var travel = tileWidth + shimmerWidth * 2;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            for (var row = firstRow; row < lastRow; row++)
            {
                var rowStart = Math.Max(firstTile, row * columns);
                var rowEnd = Math.Min(TotalTileCount, rowStart + columns);
                for (var index = rowStart; index < rowEnd; index++)
                {
                    var column = index % columns;
                    var tile = new Rect(4 + column * (tileWidth + 24), TileTopInset + row * RowPitch - ScrollOffsetY, tileWidth, tileHeight);

                    context.DrawRectangle(SurfaceBrush, SurfacePen, new RoundedRect(tile, 12), TileShadow);

                    using (context.PushTransform(Matrix.CreateTranslation(tile.X, tile.Y)))
                    using (context.PushGeometryClip(_clipGeometry!))
                    {
                        // Thumbnail well: square-bottomed so it butts against the
                        // footer the same way the real card's preview does.
                        context.DrawRectangle(ThumbBrush, null, new RoundedRect(
                            new Rect(0, 0, tileWidth, imageHeight),
                            new CornerRadius(12, 12, 0, 0)));

                        using (context.PushTransform(Matrix.CreateTranslation(
                                   (tileWidth - glyphSize) / 2,
                                   (imageHeight - glyphSize) / 2)))
                        {
                            context.DrawGeometry(GlyphBrush, null, _glyphGeometry!);
                        }

                        if (footerHeight > 22)
                        {
                            var inset = 14.0;
                            var usable = Math.Max(24, tileWidth - inset * 2);
                            var titleY = imageHeight + Math.Min(16, footerHeight * 0.24);
                            DrawBar(context, inset, titleY, usable * 0.62, 10);
                            if (footerHeight > 46) DrawBar(context, inset, titleY + 18, usable * 0.34, 8);
                        }

                        var phase = ((now - (row + column) * DiagonalStaggerSeconds) % cycle + cycle) % cycle;
                        if (phase < SweepDurationSeconds)
                        {
                            var sweep = EaseInOut(phase / SweepDurationSeconds);
                            using (context.PushTransform(Matrix.CreateTranslation(-shimmerWidth + travel * sweep, 0)))
                            {
                                context.DrawGeometry(ShimmerBrush, null, _shimmerGeometry!);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void DrawBar(DrawingContext context, double x, double y, double width, double height) =>
        context.DrawRectangle(BarBrush, null, new RoundedRect(new Rect(x, y, width, height), height / 2));

    // Slow at both ends, quick through the middle - keeps the highlight from
    // looking like a hard linear scanline.
    private static double EaseInOut(double t) => t * t * (3 - 2 * t);

    private void EnsureTileGeometry(double tileWidth, double tileHeight, double glyphSize)
    {
        if (_clipGeometry is null
            || Math.Abs(_clipGeometry.Rect.Width - tileWidth) > 0.01
            || Math.Abs(_clipGeometry.Rect.Height - tileHeight) > 0.01)
        {
            _clipGeometry = new RectangleGeometry(new Rect(0, 0, tileWidth, tileHeight))
            {
                RadiusX = 12,
                RadiusY = 12
            };
        }

        if (_glyphGeometry is not null && Math.Abs(_glyphGeometrySize - glyphSize) < 0.01) return;
        _glyphGeometrySize = glyphSize;
        // Rounded play triangle - hints at "video here" without competing with
        // the shimmer for attention.
        var radius = glyphSize / 2;
        var glyph = new StreamGeometry();
        using (var geometry = glyph.Open())
        {
            geometry.BeginFigure(new Point(radius, 0), true);
            geometry.ArcTo(new Point(radius, glyphSize), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            geometry.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            geometry.EndFigure(true);

            var triangleHeight = glyphSize * 0.40;
            var left = radius - glyphSize * 0.14;
            geometry.BeginFigure(new Point(left, radius - triangleHeight / 2), true);
            geometry.LineTo(new Point(left + glyphSize * 0.34, radius));
            geometry.LineTo(new Point(left, radius + triangleHeight / 2));
            geometry.EndFigure(true);
        }
        _glyphGeometry = glyph;
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

    private static IBrush CreateSurfaceBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#26323D"), 0),
            new GradientStop(Color.Parse("#202B35"), 1)
        }
    };

    private static IBrush CreateThumbBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.35, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#18232E"), 0),
            new GradientStop(Color.Parse("#141D25"), 1)
        }
    };

    private static IBrush CreateShimmerBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#0086A9C8"), 0),
            new GradientStop(Color.Parse("#0C86A9C8"), 0.34),
            new GradientStop(Color.Parse("#2CA9CCE6"), 0.47),
            new GradientStop(Color.Parse("#40C6E4FF"), 0.5),
            new GradientStop(Color.Parse("#2CA9CCE6"), 0.53),
            new GradientStop(Color.Parse("#0C86A9C8"), 0.66),
            new GradientStop(Color.Parse("#0086A9C8"), 1)
        }
    };
}
