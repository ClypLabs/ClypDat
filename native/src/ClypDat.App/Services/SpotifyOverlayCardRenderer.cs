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
        string? position = null, double seconds = 0, FontFamily? font = null)
    {
        using var renderer = new SpotifyCardFrames(frameWidth, frameHeight, position, font ?? ResolveFont());
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
    private readonly double _scale;
    private readonly bool _right;
    private readonly FontFamily _font;
    private readonly Dictionary<(string, double, bool, double), TextLayout> _text = new();
    private readonly Dictionary<string, Bitmap?> _art = new();
    public RenderTargetBitmap Bitmap { get; }
    public byte[] Pixels { get; }
    public int Width => Bitmap.PixelSize.Width;
    public int Height => Bitmap.PixelSize.Height;
    public SpotifyCardFrames(int width, int height, string? position, FontFamily font)
    {
        _scale = SpotifyOverlayCardRenderer.Scale(width, height);
        _right = position?.EndsWith("Right", StringComparison.OrdinalIgnoreCase) == true;
        _font = font;
        Bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(406 * _scale)), Math.Max(1, (int)Math.Ceiling(140 * _scale))), new Vector(96, 96));
        Pixels = new byte[Width * Height * 4];
    }
    private TextLayout Text(string? value, double size, bool bold = false, double width = double.PositiveInfinity)
    {
        var key = ((value ?? "").Replace('\r', ' ').Replace('\n', ' '), size, bold, width);
        if (_text.TryGetValue(key, out var layout)) return layout;
        // Timer strings change throughout long clips. Keep the cache bounded.
        layout = new TextLayout(key.Item1, new Typeface(_font, weight: bold ? FontWeight.SemiBold : FontWeight.Normal), size, Brushes.White,
            textWrapping: TextWrapping.NoWrap, textTrimming: double.IsFinite(width) ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            maxWidth: width, maxLines: 1);
        _text[key] = layout;
        return layout;
    }
    public void Render(SpotifyCard? card, double songSeconds)
    {
        if (_text.Count > 256) { foreach (var text in _text.Values) text.Dispose(); _text.Clear(); }
        if (_art.Count > 64) { foreach (var art in _art.Values) art?.Dispose(); _art.Clear(); }
        using var context = Bitmap.CreateDrawingContext();
        using (context.PushTransform(Matrix.CreateScale(_scale, _scale))) Draw(context, card, songSeconds);
    }
    private void Draw(DrawingContext context, SpotifyCard? card, double songSeconds)
    {
        if (card is null) return;
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, new Rect(0, 0, 406, 140));
        var artRect = new Rect(_right ? 266 : 0, 0, 140, 140);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 42, 42)), null, artRect);
        if (card.ArtPath is { } path)
        {
            if (!_art.TryGetValue(path, out var art))
            {
                try { art = new Bitmap(path); } catch { art = null; }
                _art[path] = art;
            }
            if (art is not null) context.DrawImage(art, new Rect(art.Size), artRect);
        }
        double left = _right ? 14 : 154;
        const double column = 238;
        var title = Text(card.Track, 28, true);
        var overflow = Math.Max(0, title.Width - column);
        var titleX = left + (overflow > 0 ? -SpotifyOverlayCardRenderer.TitleOffset(overflow, songSeconds) : _right ? column - title.Width : 0);
        using (context.PushClip(new Rect(left, 8, column, 37))) title.Draw(context, new Point(titleX, 8));
        Line(card.Artist, 21, 48, .6);
        Line(card.Album, 13, 77, .4);
        var elapsed = Text(FormatTime(card.Progress), 12);
        var duration = Text(FormatTime(card.Length), 12);
        duration.Draw(context, new Point(left + column - duration.Width, 111));
        if (card.Progress is null) return;
        elapsed.Draw(context, new Point(left, 111));
        var bar = new Rect(left + elapsed.Width + 8, 119, Math.Max(0, column - elapsed.Width - duration.Width - 16), 2);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), null, bar);
        if (card.Progress is { } played && card.Length is { TotalMilliseconds: > 0 } length)
            context.DrawRectangle(Brushes.White, null, bar.WithWidth(bar.Width * Math.Clamp(played.TotalMilliseconds / length.TotalMilliseconds, 0, 1)));
        void Line(string? value, double size, double y, double opacity)
        {
            var text = Text(value, size, width: column);
            using (context.PushOpacity(opacity)) text.Draw(context, new Point(left + (_right ? Math.Max(0, column - text.Width) : 0), y));
        }
    }
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
