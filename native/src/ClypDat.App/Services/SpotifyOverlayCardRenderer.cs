using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal sealed record SpotifyCard(string Track, string? Artist, string? Album, TimeSpan? Length, TimeSpan? Progress, string? ArtPath);

internal static class SpotifyOverlayCardRenderer
{
    public const double SourceWidth = 406, SourceHeight = 140;
    public static FontFamily ResolveFont() => Application.Current?.TryGetResource("ClypDatFontFamily", null, out var value) == true && value is FontFamily font
        ? font : new FontFamily("fonts:Inter#Inter, $Default");
    public static double Scale(int width, int height) => Math.Min(Math.Max(1, height) / 1080.0, Math.Max(1, width) / SourceWidth);
    public static string WorkPath(string purpose) => Path.Combine(AppDataPaths.Root, "spotify-cards", purpose + "-" + Guid.NewGuid().ToString("N") + ".png");
    public static double TitleOffset(double overflow, double seconds)
    {
        if (overflow <= 0) return 0;
        var travel = overflow / 24;
        var phase = Math.Max(0, seconds) % (2 * travel + 2);
        if (phase <= 1) return 0;
        if (phase <= travel + 1) return (phase - 1) * 24;
        if (phase <= travel + 2) return overflow;
        return Math.Max(0, overflow - (phase - travel - 2) * 24);
    }
    public static string? Render(SpotifyCard card, int frameHeight, string outputPath, int frameWidth = 1920,
        string? position = null, double seconds = 0, FontFamily? font = null, bool dynamicBackground = true)
    {
        using var renderer = new SpotifyCardFrames(frameWidth, frameHeight, position, font ?? ResolveFont(), dynamicBackground);
        renderer.Render(card, seconds);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        renderer.Bitmap.Save(outputPath, PngBitmapEncoderOptions.Default);
        return outputPath;
    }
}

// One renderer per preview/job. Glyph layouts and decoded artwork stay cached;
// the render target and transfer buffer are reused for every frame.
internal sealed class SpotifyCardFrames : IDisposable
{
    private const double TextWidth = 252;
    private const double TimerWidth = 48, TimerGap = 8;
    private static readonly IBrush Surface = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new(Color.FromArgb(236, 25, 30, 36), 0), new(Color.FromArgb(236, 12, 16, 21), 1) }
    };
    private static readonly Pen Outline = new(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1);
    private static readonly IBrush ArtworkSurface = new SolidColorBrush(Color.FromRgb(42, 42, 42));
    private static readonly Pen ArtworkRing = new(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
    private static readonly IBrush ProgressTrack = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
    private static readonly IBrush ProgressFill = new SolidColorBrush(Color.FromRgb(188, 233, 206));
    private static readonly IBrush[] EdgeMasks = { CreateEdgeMask(false, false), CreateEdgeMask(true, false), CreateEdgeMask(false, true), CreateEdgeMask(true, true) };
    private readonly bool _right;
    private readonly bool _dynamicBackground;
    private readonly FontFamily _font;
    private readonly Dictionary<(string, double, bool), TextLayout> _text = new();
    private sealed record Artwork(Bitmap Image, LinearGradientBrush Background) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }
    private readonly Dictionary<string, Artwork?> _art = new();
    public RenderTargetBitmap Bitmap { get; }
    public byte[] Pixels { get; }
    public int Width => Bitmap.PixelSize.Width;
    public int Height => Bitmap.PixelSize.Height;
    public SpotifyCardFrames(int width, int height, string? position, FontFamily font, bool dynamicBackground = true)
    {
        var scale = SpotifyOverlayCardRenderer.Scale(width, height);
        _right = position?.EndsWith("Right", StringComparison.OrdinalIgnoreCase) == true;
        _dynamicBackground = dynamicBackground;
        _font = font;
        Bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(406 * scale)), Math.Max(1, (int)Math.Ceiling(140 * scale))), new Vector(96, 96));
        Pixels = new byte[Width * Height * 4];
    }
    private TextLayout Text(string? value, double size, bool bold = false)
    {
        var key = ((value ?? "").Replace('\r', ' ').Replace('\n', ' '), size, bold);
        if (_text.TryGetValue(key, out var layout)) return layout;
        // Timer strings change throughout long clips. Keep the cache bounded.
        layout = new TextLayout(key.Item1, new Typeface(_font, weight: bold ? FontWeight.SemiBold : FontWeight.Normal), size, Brushes.White,
            textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.None, maxLines: 1);
        _text[key] = layout;
        return layout;
    }
    public void Render(SpotifyCard? card, double songSeconds)
    {
        if (_text.Count > 256) { foreach (var text in _text.Values) text.Dispose(); _text.Clear(); }
        if (_art.Count > 64) { foreach (var art in _art.Values) art?.Dispose(); _art.Clear(); }
        var artwork = GetArtwork(card?.ArtPath);
        using var context = Bitmap.CreateDrawingContext();
        // Fill the rounded pixel dimensions exactly. Scaling by the unrounded
        // height leaves a translucent gutter along the bottom/right at 720p etc.
        using (context.PushTransform(Matrix.CreateScale(Width / 406.0, Height / 140.0))) Draw(context, card, artwork, songSeconds);
    }
    private Artwork? GetArtwork(string? path)
    {
        if (path is null) return null;
        if (_art.TryGetValue(path, out var artwork)) return artwork;
        Bitmap? image = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            image = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 512);
            artwork = new Artwork(image, SpotifyCoverPalette.FromBitmap(image).CreateBrush());
        }
        catch { image?.Dispose(); artwork = null; }
        _art[path] = artwork;
        return artwork;
    }
    private void Draw(DrawingContext context, SpotifyCard? card, Artwork? artwork, double songSeconds)
    {
        if (card is null) return;
        if (artwork is not null) SpotifyCoverPalette.Animate(artwork.Background, _dynamicBackground ? songSeconds : 0);
        context.DrawRectangle(artwork?.Background ?? Surface, null, new Rect(0, 0, 406, 140), 20, 20);
        context.DrawRectangle(null, Outline, new Rect(.5, .5, 405, 139), 19.5, 19.5);
        var artRect = new Rect(_right ? 280 : 14, 14, 112, 112);
        context.DrawRectangle(ArtworkSurface, null, artRect, 14, 14);
        context.DrawEllipse(null, ArtworkRing, artRect.Center, 28, 28);
        context.DrawEllipse(null, ArtworkRing, artRect.Center, 20, 20);
        context.DrawEllipse(ProgressTrack, null, artRect.Center, 4, 4);
        if (artwork is not null)
        {
            var art = artwork.Image;
            var side = Math.Min(art.Size.Width, art.Size.Height);
            var source = new Rect((art.Size.Width - side) / 2, (art.Size.Height - side) / 2, side, side);
            using (context.PushClip(new RoundedRect(artRect, 14))) context.DrawImage(art, source, artRect);
        }
        context.DrawRectangle(null, Outline, artRect.Deflate(.5), 13.5, 13.5);
        double left = _right ? 14 : 140;
        Line(card.Track, 25, 17, 31, 1, bold: true);
        Line(card.Artist, 18, 51, 24, .82);
        Line(card.Album, 13, 78, 19, .58);
        var elapsed = Text(FormatTime(card.Progress), 12);
        var duration = Text(FormatTime(card.Length), 12);
        Timer(duration, left + TextWidth - TimerWidth, .68, alignRight: true);
        if (card.Progress is null) return;
        Timer(elapsed, left, .86);
        var bar = new Rect(left + TimerWidth + TimerGap, 118.5, TextWidth - 2 * (TimerWidth + TimerGap), 3);
        context.DrawRectangle(ProgressTrack, null, bar, 1.5, 1.5);
        if (card.Progress is { } played && card.Length is { TotalMilliseconds: > 0 } length)
        {
            var filled = bar.Width * Math.Clamp(played.TotalMilliseconds / length.TotalMilliseconds, 0, 1);
            if (filled > 0) context.DrawRectangle(ProgressFill, null, bar.WithWidth(filled), 1.5, 1.5);
        }
        void Timer(TextLayout text, double x, double opacity, bool alignRight = false)
        {
            // Reserve the same columns for every timestamp and font. Very long
            // durations fit inside their column without moving the progress rail.
            var scale = Math.Min(1, TimerWidth / Math.Max(1, text.Width));
            if (alignRight) x += TimerWidth - text.Width * scale;
            using (context.PushOpacity(opacity))
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, 111 + text.Height * (1 - scale) / 2)))
                text.Draw(context, default);
        }
        void Line(string? value, double size, double y, double height, double opacity, bool bold = false)
        {
            var text = Text(value, size, bold);
            var overflow = Math.Max(0, text.Width - TextWidth);
            var offset = SpotifyOverlayCardRenderer.TitleOffset(overflow, songSeconds);
            var x = left + (overflow > 0 ? -offset : _right ? TextWidth - text.Width : 0);
            var bounds = new Rect(left, y, TextWidth, height);
            using (context.PushClip(bounds))
            using (context.PushOpacity(opacity))
            {
                if (overflow <= 0) text.Draw(context, new Point(x, y));
                else
                {
                    var mask = EdgeMasks[(offset > .01 ? 1 : 0) | (overflow - offset > .01 ? 2 : 0)];
                    using (context.PushOpacityMask(mask, bounds)) text.Draw(context, new Point(x, y));
                }
            }
        }
    }
    private static IBrush CreateEdgeMask(bool left, bool right) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new(left ? Colors.Transparent : Colors.White, 0), new(Colors.White, 8 / TextWidth),
            new(Colors.White, 1 - 8 / TextWidth), new(right ? Colors.Transparent : Colors.White, 1)
        }
    };
    private static string FormatTime(TimeSpan? value) => value is not { } time ? "—:—" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    public unsafe void CopyStraightPixels()
    {
        fixed (byte* pointer = Pixels)
        {
            using var buffer = new LockedFramebuffer((nint)pointer, Bitmap.PixelSize, Width * 4, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul, null);
            Bitmap.CopyPixels(buffer);
        }
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var alpha = Pixels[i + 3];
            if (alpha == 0) { Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = 0; continue; }
            for (var c = 0; c < 3; c++) Pixels[i + c] = (byte)Math.Min(255, (Pixels[i + c] * 255 + alpha / 2) / alpha);
        }
    }
    public void Dispose()
    {
        foreach (var text in _text.Values) text.Dispose();
        foreach (var art in _art.Values) art?.Dispose();
        Bitmap.Dispose();
    }
}
