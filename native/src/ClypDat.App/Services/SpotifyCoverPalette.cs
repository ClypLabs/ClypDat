using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClypDat.App.Services;

// Sample once per decoded cover. Keep the palette independent of playback time
// so preview, seeking and export always produce the same background.
internal sealed record SpotifyCoverPalette(Color First, Color Middle, Color Last)
{
    public static unsafe SpotifyCoverPalette FromBitmap(Bitmap artwork)
    {
        const int size = 32;
        using var sample = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var side = Math.Min(artwork.Size.Width, artwork.Size.Height);
        using (var context = sample.CreateDrawingContext())
            context.DrawImage(artwork, new Rect((artwork.Size.Width - side) / 2, (artwork.Size.Height - side) / 2, side, side),
                new Rect(0, 0, size, size));
        var pixels = new byte[size * size * 4];
        fixed (byte* pointer = pixels)
        {
            using var buffer = new LockedFramebuffer((nint)pointer, sample.PixelSize, size * 4, new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul, null);
            sample.CopyPixels(buffer);
        }

        var buckets = new Dictionary<int, (int Count, int R, int G, int B)>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha < 128) continue;
            var r = Math.Min(255, pixels[i + 2] * 255 / alpha);
            var g = Math.Min(255, pixels[i + 1] * 255 / alpha);
            var b = Math.Min(255, pixels[i] * 255 / alpha);
            var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets.TryGetValue(key, out var bucket);
            buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
        }
        var colours = buckets.Values.Select(bucket =>
        {
            var colour = Color.FromRgb((byte)(bucket.R / bucket.Count), (byte)(bucket.G / bucket.Count), (byte)(bucket.B / bucket.Count));
            var brightest = Math.Max(colour.R, Math.Max(colour.G, colour.B));
            var darkest = Math.Min(colour.R, Math.Min(colour.G, colour.B));
            // Favour substantial colourful areas over black borders or white text.
            var weight = bucket.Count * (.3 + (brightest - darkest) / 255.0) * (.3 + brightest / 255.0);
            return (Colour: colour, Weight: weight);
        }).OrderByDescending(item => item.Weight).Take(64).ToArray();
        if (colours.Length == 0)
            return new(Color.FromArgb(236, 25, 30, 36), Color.FromArgb(236, 19, 24, 30), Color.FromArgb(236, 12, 16, 21));

        var first = colours[0].Colour;
        var middle = colours.MaxBy(item => item.Weight * (.05 + Distance(first, item.Colour))).Colour;
        var last = colours.MaxBy(item => item.Weight * (.05 + Math.Min(Distance(first, item.Colour), Distance(middle, item.Colour)))).Colour;
        return new(Darken(first, .26), Darken(middle, .22), Darken(last, .16));
    }

    public LinearGradientBrush CreateBrush() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new(First, 0), new(Middle, .5), new(Last, 1) }
    };

    public static void Animate(LinearGradientBrush brush, double seconds)
    {
        var phase = Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * Math.PI / 18;
        var drift = Math.Sin(phase);
        brush.StartPoint = new RelativePoint(.18 * drift, .3 * drift, RelativeUnit.Relative);
        brush.EndPoint = new RelativePoint(1 + .18 * drift, 1 - .3 * drift, RelativeUnit.Relative);
        brush.GradientStops[1].Offset = .5 + .16 * Math.Sin(phase * 2);
    }

    private static Color Darken(Color colour, double amount) => Color.FromArgb(236,
        (byte)(9 + colour.R * amount), (byte)(12 + colour.G * amount), (byte)(17 + colour.B * amount));
    private static double Distance(Color a, Color b) =>
        (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 765.0;
}
