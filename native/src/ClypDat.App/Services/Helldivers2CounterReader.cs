namespace ClypDat.App.Services;

public enum Helldivers2CounterVisibility { Unknown, Present, Absent }

public sealed record Helldivers2CounterReading(
    Helldivers2CounterVisibility Visibility, int? Count, string Text, double SkullScore);

/// <summary>Shared HUD reader for live frames and recorded fixtures.</summary>
public sealed class Helldivers2CounterReader
{
    private readonly GrayTemplateMatcher _skull;
    private readonly Func<GrayDetectorImage, Task<string>> _readText;

    public Helldivers2CounterReader(Func<GrayDetectorImage, Task<string>> readText, string? templateRoot = null)
    {
        _readText = readText;
        _skull = GrayTemplateMatcher.FromGray(GrayPng.Read(Path.Combine(
            templateRoot ?? DetectorTemplates.DefaultRoot, "helldivers2", "skull.png")), TemplateScoring.HighPass3);
    }

    public async Task<Helldivers2CounterReading> ReadAsync(GrayDetectorImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height)
            return new(Helldivers2CounterVisibility.Unknown, null, string.Empty, 0);

        var hud = Resize(image, 308, 174);
        var score = 0.0;
        for (var size = 32; size <= 64; size += 2)
        for (var y = 72; y <= 82; y += 2)
        for (var x = 90; x <= 100; x += 2)
            score = Math.Max(score, _skull.Score(Crop(hud, x - size / 2, y - size / 2, size, size)));

        var visibility = score >= 0.50 ? Helldivers2CounterVisibility.Present
            : score <= 0.25 ? Helldivers2CounterVisibility.Absent : Helldivers2CounterVisibility.Unknown;
        if (visibility != Helldivers2CounterVisibility.Present)
            return new(visibility, null, string.Empty, score);

        var text = await _readText(PrepareNumber(hud)).ConfigureAwait(false);
        if (Helldivers2Detector.TryParseKillCounter(text, out var count))
            return new(visibility, count, text, score);

        // At smaller resolutions OCR often treats the multiplier as part of a
        // digit. Retry the digits alone; never accept bare numbers from the
        // wider crop where an "x" misread as "8" would invent a hundred-tier.
        var digits = (await _readText(PrepareNumber(hud, digitsOnly: true)).ConfigureAwait(false)).Trim();
        return new(visibility, digits.Length is >= 1 and <= 3 && digits.All(char.IsAsciiDigit)
            ? int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) : null, digits, score);
    }

    internal static GrayDetectorImage PrepareNumber(GrayDetectorImage hud, bool digitsOnly = false)
    {
        // Keep the multiplier and up to three digits; exclude bright scenery
        // above/below the text, which makes Windows OCR return an empty line.
        var crop = digitsOnly ? Crop(hud, 142, 66, 90, 32) : Crop(hud, 120, 56, 112, 48);
        const int scale = 4, padding = 20;
        var width = crop.Width * scale + padding * 2;
        var height = crop.Height * scale + padding * 2;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < crop.Height * scale; y++)
        for (var x = 0; x < crop.Width * scale; x++)
            pixels[(y + padding) * width + x + padding] = crop.Pixels[y / scale * crop.Width + x / scale] > 140
                ? (byte)0 : (byte)255;
        return new(width, height, pixels);
    }

    internal static GrayDetectorImage Resize(GrayDetectorImage source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return source;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Pixel-centred bilinear scaling preserves thin digits at 1080p.
            var sx = Math.Clamp((x + 0.5) * source.Width / width - 0.5, 0, source.Width - 1);
            var sy = Math.Clamp((y + 0.5) * source.Height / height - 0.5, 0, source.Height - 1);
            var x0 = (int)sx;
            var y0 = (int)sy;
            var x1 = Math.Min(x0 + 1, source.Width - 1);
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var top = source.Pixels[y0 * source.Width + x0] * (1 - (sx - x0)) + source.Pixels[y0 * source.Width + x1] * (sx - x0);
            var bottom = source.Pixels[y1 * source.Width + x0] * (1 - (sx - x0)) + source.Pixels[y1 * source.Width + x1] * (sx - x0);
            pixels[y * width + x] = (byte)Math.Round(top * (1 - (sy - y0)) + bottom * (sy - y0));
        }
        return new(width, height, pixels);
    }

    private static GrayDetectorImage Crop(GrayDetectorImage source, int x, int y, int width, int height)
    {
        var pixels = new byte[width * height];
        for (var row = 0; row < height; row++)
            Array.Copy(source.Pixels, (y + row) * source.Width + x, pixels, row * width, width);
        return new(width, height, pixels);
    }
}
