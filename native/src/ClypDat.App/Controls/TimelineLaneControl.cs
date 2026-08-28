using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

public sealed class TimelineLaneControl : Control
{
    public static readonly StyledProperty<string> LaneBrushProperty =
        AvaloniaProperty.Register<TimelineLaneControl, string>(nameof(LaneBrush), "#24313B");

    public static readonly StyledProperty<bool> IsVideoProperty =
        AvaloniaProperty.Register<TimelineLaneControl, bool>(nameof(IsVideo));

    public static readonly StyledProperty<IReadOnlyList<double>?> PeaksProperty =
        AvaloniaProperty.Register<TimelineLaneControl, IReadOnlyList<double>?>(nameof(Peaks));

    public static readonly StyledProperty<Bitmap?> FilmstripProperty =
        AvaloniaProperty.Register<TimelineLaneControl, Bitmap?>(nameof(Filmstrip));

    public static readonly StyledProperty<double> TrimStartPercentProperty =
        AvaloniaProperty.Register<TimelineLaneControl, double>(nameof(TrimStartPercent));

    public static readonly StyledProperty<double> TrimEndPercentProperty =
        AvaloniaProperty.Register<TimelineLaneControl, double>(nameof(TrimEndPercent), 100);

    public string LaneBrush
    {
        get => GetValue(LaneBrushProperty);
        set => SetValue(LaneBrushProperty, value);
    }

    public bool IsVideo
    {
        get => GetValue(IsVideoProperty);
        set => SetValue(IsVideoProperty, value);
    }

    public IReadOnlyList<double>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public Bitmap? Filmstrip
    {
        get => GetValue(FilmstripProperty);
        set => SetValue(FilmstripProperty, value);
    }

    public double TrimStartPercent
    {
        get => GetValue(TrimStartPercentProperty);
        set => SetValue(TrimStartPercentProperty, value);
    }

    public double TrimEndPercent
    {
        get => GetValue(TrimEndPercentProperty);
        set => SetValue(TrimEndPercentProperty, value);
    }

    static TimelineLaneControl()
    {
        AffectsRender<TimelineLaneControl>(
            LaneBrushProperty,
            IsVideoProperty,
            PeaksProperty,
            FilmstripProperty,
            TrimStartPercentProperty,
            TrimEndPercentProperty);
    }

    // Every one of these used to be rebuilt on EVERY render - two or three
    // Color.Parse calls over a string, fresh SolidColorBrush/Pen objects, and
    // (worst) a from-scratch StreamGeometry over all 700 waveform peaks. That
    // is not a once-per-open cost: AffectsRender includes the trim percentages
    // and every lane binds them, so dragging a trim handle re-renders every
    // lane on every single pointer-move.
    private static readonly Pen VideoOutlinePen = new(AppThemeService.Brush("Semantic_13C8B5", "#13C8B5"), 1);
    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.FromArgb(120, 10, 15, 19));
    private static readonly IBrush HatchBrush = CreateHatchBrush();

    private string? _cachedLaneBrushKey;
    private IBrush? _cachedLaneFill;
    private IBrush? _cachedWaveformFill;

    private IReadOnlyList<double>? _cachedPeaks;
    private Size _cachedPeaksSize;
    private StreamGeometry? _cachedWaveformGeometry;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        EnsureLaneBrushes();
        var radius = new CornerRadius(3);
        context.DrawRectangle(_cachedLaneFill, null, rect, radius.TopLeft, radius.TopLeft);

        if (IsVideo)
        {
            DrawFilmstrip(context, rect);
            context.DrawRectangle(null, VideoOutlinePen, rect.Deflate(1), 3, 3);
        }
        else
        {
            DrawWaveform(context, rect);
        }

        DrawTrimShade(context, rect);
    }

    // LaneBrush is a string that changes only when the track it represents
    // changes, so both brushes derived from it are rebuilt on that transition
    // rather than per render.
    private void EnsureLaneBrushes()
    {
        var lane = LaneBrush;
        if (_cachedLaneFill is not null && _cachedWaveformFill is not null && _cachedLaneBrushKey == lane) return;

        _cachedLaneBrushKey = lane;
        _cachedLaneFill = new SolidColorBrush(ParseColor(lane, "#24313B"));
        var waveColor = ParseColor(lane, "#FFFFFF");
        _cachedWaveformFill = new SolidColorBrush(
            Color.FromArgb(190, Lighten(waveColor.R), Lighten(waveColor.G), Lighten(waveColor.B)));
    }

    // Filmstrip is cached as ONE spritesheet image (MediaProbeService.
    // EnsureFilmstripAsync) holding FilmstripFrameCount frames tiled left-
    // to-right - read here as a spritesheet, slicing out each frame's own
    // source sub-rect (bitmap.Width/FilmstripFrameCount wide) and drawing it
    // individually into its own on-screen cell. Frame count is decoupled
    // from the lane's actual pixel width (like DrawWaveform's peaks array):
    // whatever the lane's current width is, the fixed frame count gets
    // stretched evenly across it, edge to edge.
    private void DrawFilmstrip(DrawingContext context, Rect rect)
    {
        var sheet = Filmstrip;
        if (sheet is null) return;

        var frameCount = MediaProbeService.FilmstripFrameCount;
        var sourceFrameWidth = sheet.Size.Width / frameCount;

        using (context.PushClip(rect.Deflate(1)))
        {
            var destFrameWidth = rect.Width / frameCount;
            for (var i = 0; i < frameCount; i++)
            {
                var source = new Rect(i * sourceFrameWidth, 0, sourceFrameWidth, sheet.Size.Height);
                // +0.5 overlap so float rounding between adjacent frame
                // rects can't leave a hairline gap of bare lane background
                // showing through between two frames.
                var dest = new Rect(rect.X + i * destFrameWidth, rect.Y, destFrameWidth + 0.5, rect.Height);
                // Cropped ("cover"), not stretched to fill both dimensions
                // independently - each cell's own width/height ratio rarely
                // matches a source frame's 16:9, and drawing the WHOLE
                // source slice into a mismatched dest rect distorts it
                // (squished/stretched). Crop the source slice to the cell's
                // own aspect ratio first, then draw that crop filling the
                // cell exactly - same idea as CSS's object-fit: cover.
                context.DrawImage(sheet, CoverSourceRect(source, dest.Size), dest);
            }
        }
    }

    // Largest centered crop of sourceRect (a sub-region of the spritesheet,
    // one frame's own slice - not necessarily anchored at the bitmap's
    // origin) that matches destSize's aspect ratio - crops the wider
    // dimension (left/right or top/bottom evenly) rather than distorting
    // either axis independently.
    private static Rect CoverSourceRect(Rect sourceRect, Size destSize)
    {
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || destSize.Width <= 0 || destSize.Height <= 0)
        {
            return sourceRect;
        }

        var sourceAspect = sourceRect.Width / sourceRect.Height;
        var destAspect = destSize.Width / destSize.Height;

        if (sourceAspect > destAspect)
        {
            // Source slice is relatively wider than the cell - crop its
            // sides, keep full height.
            var cropWidth = sourceRect.Height * destAspect;
            return new Rect(sourceRect.X + (sourceRect.Width - cropWidth) / 2, sourceRect.Y, cropWidth, sourceRect.Height);
        }

        // Source slice is relatively taller/narrower than the cell - crop
        // top/bottom, keep full width.
        var cropHeight = sourceRect.Width / destAspect;
        return new Rect(sourceRect.X, sourceRect.Y + (sourceRect.Height - cropHeight) / 2, sourceRect.Width, cropHeight);
    }

    private void DrawWaveform(DrawingContext context, Rect rect)
    {
        var peaks = Peaks;
        if (peaks is null || peaks.Count == 0) return;

        context.DrawGeometry(_cachedWaveformFill, null, EnsureWaveformGeometry(peaks, rect));
    }

    // The shape depends only on the peaks and the lane's size, neither of which
    // changes while a trim handle is being dragged - which is exactly when this
    // is re-rendered most. Rebuilding ~1400 line segments per pointer-move was
    // pure waste.
    private StreamGeometry EnsureWaveformGeometry(IReadOnlyList<double> peaks, Rect rect)
    {
        if (_cachedWaveformGeometry is not null &&
            ReferenceEquals(_cachedPeaks, peaks) &&
            _cachedPeaksSize == rect.Size)
        {
            return _cachedWaveformGeometry;
        }

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var mid = rect.Height / 2;
            var max = Math.Max(0.7, rect.Height * 0.36);
            var count = peaks.Count;
            for (var i = 0; i < count; i++)
            {
                var x = count == 1 ? 0 : i * rect.Width / (count - 1);
                var y = mid - Math.Clamp(peaks[i], 0, 1) * max;
                if (i == 0) stream.BeginFigure(new Point(x, y), true);
                else stream.LineTo(new Point(x, y));
            }

            for (var i = count - 1; i >= 0; i--)
            {
                var x = count == 1 ? 0 : i * rect.Width / (count - 1);
                var y = mid + Math.Clamp(peaks[i], 0, 1) * max;
                stream.LineTo(new Point(x, y));
            }
            stream.EndFigure(true);
        }

        _cachedPeaks = peaks;
        _cachedPeaksSize = rect.Size;
        _cachedWaveformGeometry = geometry;
        return geometry;
    }

    private void DrawTrimShade(DrawingContext context, Rect rect)
    {
        var start = Math.Clamp(TrimStartPercent, 0, 100) / 100 * rect.Width;
        var end = Math.Clamp(TrimEndPercent, 0, 100) / 100 * rect.Width;
        if (start > 0) DrawShade(context, new Rect(0, 0, start, rect.Height));
        if (end < rect.Width) DrawShade(context, new Rect(end, 0, rect.Width - end, rect.Height));
    }

    private static void DrawShade(DrawingContext context, Rect rect)
    {
        if (rect.Width <= 0) return;
        using (context.PushClip(rect))
        {
            context.DrawRectangle(ShadeBrush, null, rect);
            // One tiled fill instead of the ~110 individual DrawLine calls the
            // 16px-step diagonal loop used to issue per shaded side, per render.
            context.DrawRectangle(HatchBrush, null, rect);
        }
    }

    // 16x16 tile carrying a single 45-degree stroke corner to corner; tiled, it
    // reproduces the same continuous diagonal hatch at the same 16px spacing.
    private static IBrush CreateHatchBrush()
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(0, 16), false);
            stream.LineTo(new Point(16, 0));
            stream.EndFigure(false);
        }

        return new DrawingBrush
        {
            Drawing = new GeometryDrawing
            {
                Geometry = geometry,
                Pen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1)
            },
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            Stretch = Stretch.None
        };
    }

    private static byte Lighten(byte channel) => (byte)Math.Min(255, channel + 58);

    private static Color ParseColor(string value, string fallback)
    {
        try
        {
            return Color.Parse(string.IsNullOrWhiteSpace(value) ? fallback : value);
        }
        catch
        {
            return Color.Parse(fallback);
        }
    }
}
