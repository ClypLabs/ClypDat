using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Immutable input for Spotify card rendering.</summary>
internal sealed record SpotifyCard(string Track, string? Artist, string? Album, TimeSpan? Length, TimeSpan? Progress, string? ArtPath);

/// <summary>Renders the Spotify widget used by preview, export and burn-in.</summary>
internal static class SpotifyOverlayCardRenderer
{
    public const double SourceWidth = 580;
    public const double SourceHeight = 200;
    private const double ReferenceHeight = 1080;

    // Each caller gets a private file. A burn and export may overlap.
    public static string WorkPath(string purpose) => Path.Combine(AppDataPaths.Root, "spotify-cards", purpose + "-" + Guid.NewGuid().ToString("N") + ".png");

    public static string? Render(SpotifyCard card, int frameHeight, string outputPath, int frameWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(card.Track)) return null;
        var scale = Math.Max(.1, (frameHeight > 0 ? frameHeight : ReferenceHeight) / ReferenceHeight);
        // Preserve proportional margins on narrow footage.
        if (frameWidth > 0) scale = Math.Min(scale, Math.Max(.1, frameWidth * .93 / SourceWidth));
        var width = (int)Math.Ceiling(SourceWidth * scale);
        var height = (int)Math.Ceiling(SourceHeight * scale);

        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using var context = target.CreateDrawingContext();
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, new Rect(0, 0, SourceWidth, SourceHeight), 25, 25);
            DrawArt(context, card.ArtPath, new Rect(0, 0, 200, 200));

            const double left = 220, textWidth = 340;
            DrawText(context, card.Track, left, 18, textWidth, 40, FontWeight.SemiBold, Brushes.White);
            DrawText(context, card.Artist, left, 70, textWidth, 30, FontWeight.Normal, new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)));
            DrawText(context, card.Album, left, 110, textWidth, 18, FontWeight.Normal, new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)));
            DrawText(context, FormatTime(card.Progress), left, 157, 55, 16, FontWeight.Normal, Brushes.White);
            DrawText(context, FormatTime(card.Length), 522, 157, 55, 16, FontWeight.Normal, Brushes.White, TextAlignment.Right);
            DrawProgress(context, new Rect(280, 169, 226, 3), card.Progress, card.Length);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        target.Save(outputPath);
        return outputPath;
    }

    private static void DrawText(DrawingContext context, string? value, double x, double y, double width, double size, FontWeight weight, IBrush brush, TextAlignment alignment = TextAlignment.Left)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Roboto"), FontStyle.Normal, weight), size, brush)
        { MaxTextWidth = width, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment };
        context.DrawText(text, new Point(x, y));
    }

    private static void DrawArt(DrawingContext context, string? artPath, Rect rect)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 42, 42)), null, rect, 25, 25);
        if (string.IsNullOrWhiteSpace(artPath) || !File.Exists(artPath)) return;
        try
        {
            using var art = new Bitmap(artPath);
            using (context.PushClip(rect)) context.DrawImage(art, new Rect(0, 0, art.PixelSize.Width, art.PixelSize.Height), rect);
        }
        catch (Exception error) { AppLog.Error($"Spotify: could not draw cover art '{artPath}'.", error); }
    }

    private static void DrawProgress(DrawingContext context, Rect rect, TimeSpan? progress, TimeSpan? length)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), null, rect, 1.5, 1.5);
        if (progress is not { } played || length is not { TotalMilliseconds: > 0 } total) return;
        var filled = rect.Width * Math.Clamp(played.TotalMilliseconds / total.TotalMilliseconds, 0, 1);
        if (filled > 0) context.DrawRectangle(Brushes.White, null, new Rect(rect.X, rect.Y, filled, rect.Height), 1.5, 1.5);
    }

    private static string FormatTime(TimeSpan? value) => value is not { } time ? "—:—" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
