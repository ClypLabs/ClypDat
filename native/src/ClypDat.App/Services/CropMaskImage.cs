using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Security.Cryptography;
using System.Text;

namespace ClypDat.App.Services;

/// <summary>
/// Renders the editor's crop guide - the frame dimmed outside the kept
/// rectangle - to a PNG for libvlc's logo filter to composite into the picture.
/// </summary>
/// <remarks>
/// This exists because an overlay window cannot do the job. Drawing the guide in
/// an owned window above the video was tried first, and the translucent part of
/// it flickered constantly while the opaque outline sat perfectly still - the
/// giveaway that the problem is per-pixel alpha, not z-order. libvlc's video
/// output presents through a flip-model swapchain, which bypasses DWM's
/// redirection for that region, so a window blending against what is underneath
/// has nothing stable to blend against. Anything drawn ON the picture has to go
/// through the thing that draws the picture.
///
/// The image is written at the clip's own resolution: the logo filter positions
/// its image in video coordinates and never scales it, so a smaller mask would
/// sit in the corner rather than covering the frame.
/// </remarks>
public static class CropMaskImage
{
    // Matches the shade the overlay used, and stays a guide rather than a
    // letterbox: the part being cut has to remain readable so it can be aimed.
    private const byte ShadeAlpha = 0x66;
    private const int OutlineThickness = 2;
    // ClypDat green. The opaque guide stays legible over both the dimmed and
    // retained parts of the frame.
    private static readonly uint OutlineColor = Bgra(0xFF, 0x38, 0xD9, 0x96);

    /// <summary>
    /// Writes (or reuses) the mask PNG for this crop and returns its path.
    /// </summary>
    public static string? TryWrite(string directory, ClipRenderFilters.CropRect crop, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crop-{Key(crop, sourceWidth, sourceHeight)}.png");
            // Keyed by the geometry itself, so dragging a position slider back
            // and forth re-uses files already written instead of re-encoding a
            // full-resolution image for a crop that has been seen before.
            if (File.Exists(path))
            {
                // Reused masks participate in the bounded recency cache too.
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
                return path;
            }

            using var bitmap = new WriteableBitmap(
                new PixelSize(sourceWidth, sourceHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var frame = bitmap.Lock())
            {
                Fill(frame, crop, sourceWidth, sourceHeight);
            }

            bitmap.Save(path);
            return path;
        }
        catch (Exception error)
        {
            AppLog.Error("Crop mask render failed", error);
            return null;
        }
    }

    /// <summary>
    /// Bounds the recency cache. They are flat-colour PNGs, but at a 4K clip's
    /// resolution they still add up over a long editing session.
    /// </summary>
    public static void Prune(string directory, string? keepPath, int maximumFiles = 32)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = Directory.EnumerateFiles(directory, "crop-*.png")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            foreach (var file in files.Skip(Math.Max(0, maximumFiles)))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Crop mask prune failed", error);
        }
    }

    private static unsafe void Fill(ILockedFramebuffer frame, ClipRenderFilters.CropRect crop, int width, int height)
    {
        var shade = Bgra(ShadeAlpha, 0, 0, 0);
        const uint Clear = 0;

        var keepLeft = Math.Clamp(crop.X, 0, width);
        var keepTop = Math.Clamp(crop.Y, 0, height);
        var keepRight = Math.Clamp(crop.X + crop.Width, 0, width);
        var keepBottom = Math.Clamp(crop.Y + crop.Height, 0, height);

        for (var y = 0; y < height; y++)
        {
            var row = (uint*)(frame.Address + y * frame.RowBytes);
            var insideRows = y >= keepTop && y < keepBottom;
            if (!insideRows)
            {
                for (var x = 0; x < width; x++) row[x] = shade;
                continue;
            }

            for (var x = 0; x < keepLeft; x++) row[x] = shade;
            for (var x = keepLeft; x < keepRight; x++) row[x] = Clear;
            for (var x = keepRight; x < width; x++) row[x] = shade;
        }

        // Outline last, so it sits on top of the shading rather than being
        // overwritten by it.
        var thickness = Math.Max(1, (int)Math.Round(OutlineThickness * (height / 1080.0)));
        for (var y = keepTop; y < keepBottom; y++)
        {
            var row = (uint*)(frame.Address + y * frame.RowBytes);
            var onHorizontalEdge = y < keepTop + thickness || y >= keepBottom - thickness;
            if (onHorizontalEdge)
            {
                for (var x = keepLeft; x < keepRight; x++) row[x] = OutlineColor;
                continue;
            }

            for (var x = keepLeft; x < Math.Min(keepLeft + thickness, keepRight); x++) row[x] = OutlineColor;
            for (var x = Math.Max(keepLeft, keepRight - thickness); x < keepRight; x++) row[x] = OutlineColor;
        }
    }

    private static uint Bgra(byte a, byte r, byte g, byte b) =>
        (uint)((a << 24) | (r << 16) | (g << 8) | b);

    private static string Key(ClipRenderFilters.CropRect crop, int width, int height)
    {
        var input = $"{width}x{height}|{crop.X},{crop.Y},{crop.Width},{crop.Height}|{ShadeAlpha}|{OutlineColor:X8}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    }
}
