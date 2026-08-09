using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.ViewModels;

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

    public static readonly StyledProperty<double> TileTopInsetProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(TileTopInset));

    public static readonly StyledProperty<double> RowPitchProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(RowPitch));

    public static readonly StyledProperty<double> ScrollOffsetYProperty =
        AvaloniaProperty.Register<LibraryLoadingTilesOverlay, double>(nameof(ScrollOffsetY));

    private const double SweepDurationSeconds = 1.6;
    private const double PauseDurationSeconds = 0.6;
    // Each tile starts its sweep slightly after the previous row-major tile,
    // matching restore order while still reading as a diagonal wave.
    private const double DiagonalStaggerSeconds = 0.07;
    // Horizontal shear per unit of tile height; negative leans the band's top
    // edge ahead of its bottom, roughly a 27 degree tilt.
    private const double ShearFactor = -0.5;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<LibraryLoadingTilesOverlay> ActiveOverlays = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private static readonly IBrush SurfaceBrush = CreateSurfaceBrush();
    private static readonly IPen SurfacePen = new Pen(new SolidColorBrush(Color.Parse("#1AFFFFFF")), 1);
    private static readonly IBrush ShimmerBrush = CreateShimmerBrush();
    private static readonly BoxShadows TileShadow = new(new BoxShadow
    {
        OffsetX = 0,
        OffsetY = 6,
        Blur = 18,
        Spread = -6,
        Color = Color.Parse("#66000000")
    });
    private RectangleGeometry? _clipGeometry;

    static LibraryLoadingTilesOverlay()
    {
        AffectsRender<LibraryLoadingTilesOverlay>(
            TotalTileCountProperty,
            LoadedTileCountProperty,
            ColumnCountProperty,
            TileWidthProperty,
            TileHeightProperty,
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
        var shimmerWidth = tileWidth * 1.3;
        EnsureTileGeometry(tileWidth, tileHeight);
        // The band is drawn axis-aligned and sheared, so the gradient's own
        // contour lines lean with it - shearing only the outline would leave
        // the falloff running straight up and down inside a slanted shape.
        var slantSpan = Math.Abs(ShearFactor) * tileHeight;
        var shimmerRect = new Rect(0, 0, shimmerWidth, tileHeight);
        var startX = -shimmerWidth - slantSpan;
        var travel = tileWidth + shimmerWidth * 2 + slantSpan * 2;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            for (var row = firstRow; row < lastRow; row++)
            {
                var rowStart = Math.Max(firstTile, row * columns);
                var rowEnd = Math.Min(TotalTileCount, (row + 1) * columns);
                for (var index = rowStart; index < rowEnd; index++)
                {
                    var column = index % columns;
                    var tile = new Rect(
                        LibraryCardLayoutCalculator.CardLeftInset + column * LibraryCardLayoutCalculator.SlotWidth(tileWidth),
                        TileTopInset + row * RowPitch - ScrollOffsetY,
                        tileWidth,
                        tileHeight);

                    context.DrawRectangle(SurfaceBrush, SurfacePen, new RoundedRect(tile, 12), TileShadow);

                    var phase = ((now - index * DiagonalStaggerSeconds) % cycle + cycle) % cycle;
                    if (phase >= SweepDurationSeconds) continue;

                    using (context.PushTransform(Matrix.CreateTranslation(tile.X, tile.Y)))
                    using (context.PushGeometryClip(_clipGeometry!))
                    {
                        // Fades in and back out across the pass so the band never
                        // pops at the tile edges, only in the middle of travel.
                        var t = phase / SweepDurationSeconds;
                        var opacity = Math.Sin(Math.PI * t);
                        opacity *= opacity;
                        using (context.PushOpacity(opacity))
                        using (context.PushTransform(new Matrix(1, 0, ShearFactor, 1, startX + travel * EaseInOut(t), 0)))
                        {
                            context.DrawRectangle(ShimmerBrush, null, shimmerRect);
                        }
                    }
                }
            }
        }
    }

    // Slow at both ends, quick through the middle - keeps the highlight from
    // looking like a hard linear scanline.
    private static double EaseInOut(double t) => t * t * (3 - 2 * t);

    private void EnsureTileGeometry(double tileWidth, double tileHeight)
    {
        if (_clipGeometry is not null
            && Math.Abs(_clipGeometry.Rect.Width - tileWidth) < 0.01
            && Math.Abs(_clipGeometry.Rect.Height - tileHeight) < 0.01) return;

        _clipGeometry = new RectangleGeometry(new Rect(0, 0, tileWidth, tileHeight))
        {
            RadiusX = 12,
            RadiusY = 12
        };
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

    // Sampled as a gaussian rather than hand-placed stops: a handful of stops
    // leaves visible banding edges on a band this wide, 33 of them reads as a
    // continuous falloff. Colour warms from steel to near-white at the peak.
    private static IBrush CreateShimmerBrush()
    {
        const int samples = 33;
        const double sigma = 0.13;
        const double peakAlpha = 0.30;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        for (var i = 0; i < samples; i++)
        {
            var offset = i / (double)(samples - 1);
            var distance = (offset - 0.5) / sigma;
            var weight = Math.Exp(-0.5 * distance * distance);
            // Tail is clipped to exactly zero at the ends so the band cannot
            // leave a faint rectangle behind it.
            weight = Math.Max(0, (weight - 0.02) / 0.98);
            var alpha = (byte)Math.Round(peakAlpha * weight * 255);
            var tint = weight * weight;
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(
                    alpha,
                    Lerp(0x7E, 0xE8, tint),
                    Lerp(0x9E, 0xF2, tint),
                    Lerp(0xBA, 0xFF, tint)),
                offset));
        }
        return brush;
    }

    private static byte Lerp(int from, int to, double t) => (byte)Math.Round(from + (to - from) * t);
}
