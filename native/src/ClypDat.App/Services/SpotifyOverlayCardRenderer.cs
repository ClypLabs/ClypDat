using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>What a card needs to draw itself.</summary>
internal sealed record SpotifyCard(string Track, string? Artist, TimeSpan? Length, TimeSpan? Progress, string? ArtPath);

/// <summary>
/// Draws the now-playing card to a PNG, for ffmpeg to composite over a clip.
///
/// A rendered image rather than ffmpeg's own drawtext: the card is a rounded
/// plate, cover art, two weights of text and a progress bar, and drawtext can
/// express roughly the first line of that. Rendering it here also means the
/// export and the editor preview can be the same picture rather than two
/// approximations of one design.
/// </summary>
internal static class SpotifyOverlayCardRenderer
{
    // Everything is a fraction of the clip's height, so a 1080p export and a
    // 1440p one get the same card rather than the same pixel counts.
    private const double CardHeightFraction = 0.104;
    private const double MinimumCardHeight = 84;

    /// <summary>
    /// Where a rendered card lives. Under the app's own data rather than %TEMP%
    /// so a cleaner cannot delete it mid-encode, and one file per purpose so a
    /// burn running behind an export cannot overwrite the card the export is
    /// still reading.
    /// </summary>
    public static string WorkPath(string purpose) =>
        Path.Combine(AppDataPaths.Root, "spotify-cards", $"{purpose}.png");

    /// <summary>Renders the card and returns the file it was written to.</summary>
    public static string? Render(SpotifyCard card, int frameHeight, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(card.Track)) return null;

        var height = Math.Max(MinimumCardHeight, (frameHeight > 0 ? frameHeight : 1080) * CardHeightFraction);
        var padding = height * 0.14;
        var artSize = height - padding * 2;
        var titleSize = height * 0.215;
        var artistSize = height * 0.17;
        var gap = height * 0.16;

        var title = FormatText(card.Track, titleSize, FontWeight.Bold, Brushes.White);
        var artist = string.IsNullOrWhiteSpace(card.Artist)
            ? null
            : FormatText(card.Artist!, artistSize, FontWeight.SemiBold, new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)));

        // Wide enough for the longest line, capped so a long title cannot run
        // the card off the side of the frame.
        var textWidth = Math.Max(title.Width, artist?.Width ?? 0);
        var maximumText = height * 5.2;
        if (textWidth > maximumText)
        {
            title.MaxTextWidth = maximumText;
            if (artist is not null) artist.MaxTextWidth = maximumText;
            textWidth = maximumText;
        }

        var width = padding + artSize + gap + textWidth + padding;
        var pixelSize = new PixelSize((int)Math.Ceiling(width), (int)Math.Ceiling(height));

        using var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            var bounds = new Rect(0, 0, pixelSize.Width, pixelSize.Height);
            var radius = height * 0.22;
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x18, 0x18, 0x1B)), null, bounds, radius, radius);

            var artRect = new Rect(padding, padding, artSize, artSize);
            DrawArt(context, card.ArtPath, artRect, height * 0.12);

            var textLeft = artRect.Right + gap;
            // Title, artist and bar as one block centred against the art rather
            // than against the card: with no artist the pair would otherwise sit
            // high and leave the card looking bottom-heavy.
            var barHeight = Math.Max(3, height * 0.055);
            var barTop = artRect.Bottom - barHeight;
            context.DrawText(title, new Point(textLeft, padding));
            if (artist is not null) context.DrawText(artist, new Point(textLeft, padding + title.Height + height * 0.04));

            DrawProgress(context, new Rect(textLeft, barTop, textWidth, barHeight), card.Progress, card.Length);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        target.Save(outputPath);
        return outputPath;
    }

    private static FormattedText FormatText(string text, double size, FontWeight weight, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush)
        {
            TextAlignment = TextAlignment.Left,
            Trimming = TextTrimming.CharacterEllipsis
        };

    private static void DrawArt(DrawingContext context, string? artPath, Rect rect, double radius)
    {
        // A plate either way: a missing cover leaves a square of the card's own
        // colour rather than a hole where the art should be.
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)), null, rect, radius, radius);
        if (string.IsNullOrWhiteSpace(artPath) || !File.Exists(artPath)) return;

        try
        {
            using var art = new Bitmap(artPath);
            using (context.PushClip(rect))
            {
                context.DrawImage(art, new Rect(0, 0, art.PixelSize.Width, art.PixelSize.Height), rect);
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"Spotify: could not draw the cover art from '{artPath}'.", error);
        }
    }

    private static void DrawProgress(DrawingContext context, Rect rect, TimeSpan? progress, TimeSpan? length)
    {
        var radius = rect.Height / 2;
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)), null, rect, radius, radius);

        if (progress is not { } played || length is not { TotalMilliseconds: > 0 } total) return;
        var fraction = Math.Clamp(played.TotalMilliseconds / total.TotalMilliseconds, 0, 1);
        if (fraction <= 0) return;

        // Rounded at both ends, so a barely-started track still reads as a bar
        // rather than as a sliver.
        var filled = Math.Max(rect.Height, rect.Width * fraction);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)), null,
            new Rect(rect.X, rect.Y, filled, rect.Height), radius, radius);
    }
}
