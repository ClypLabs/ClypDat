using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Eve.App.Services;

namespace Eve.App.Controls;

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

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var radius = new CornerRadius(3);
        var baseColor = ParseColor(LaneBrush, "#24313B");
        var brush = new SolidColorBrush(baseColor);
        context.DrawRectangle(brush, null, rect, radius.TopLeft, radius.TopLeft);

        if (IsVideo)
        {
            DrawFilmstrip(context, rect);
            context.DrawRectangle(null, new Pen(Color.Parse("#13C8B5").ToUInt32()), rect.Deflate(1), 3, 3);
        }
        else
        {
            DrawWaveform(context, rect);
        }

        DrawTrimShade(context, rect);
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

        var color = ParseColor(LaneBrush, "#FFFFFF");
        var waveform = new SolidColorBrush(Color.FromArgb(190, Lighten(color.R), Lighten(color.G), Lighten(color.B)));
        context.DrawGeometry(waveform, null, geometry);
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
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 10, 15, 19)), null, rect);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
            for (var x = -rect.Height; x < rect.Width + rect.Height; x += 16)
            {
                context.DrawLine(pen, new Point(rect.X + x, rect.Bottom), new Point(rect.X + x + rect.Height, rect.Y));
            }
        }
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
