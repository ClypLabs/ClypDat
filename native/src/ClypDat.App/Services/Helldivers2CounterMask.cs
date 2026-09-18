namespace ClypDat.App.Services;

/// <summary>Colour isolation shared by capture and recorded counter fixtures.</summary>
public static class Helldivers2CounterMask
{
    public static GrayDetectorImage FromBgra(ReadOnlySpan<byte> pixels, int stride, PixelRegion crop)
    {
        ValidatePlane(pixels.Length, stride, crop, 4);
        var mask = new byte[checked(crop.Width * crop.Height)];
        for (var y = 0; y < crop.Height; y++)
        for (var x = 0; x < crop.Width; x++)
        {
            var offset = (crop.Y + y) * stride + (crop.X + x) * 4;
            mask[y * crop.Width + x] = Select(pixels[offset + 2], pixels[offset + 1], pixels[offset], x, crop.Width);
        }
        return new(crop.Width, crop.Height, mask);
    }

    public static GrayDetectorImage FromNv12(ReadOnlySpan<byte> luminance, int yStride,
        ReadOnlySpan<byte> chroma, int uvStride, PixelRegion crop)
    {
        ValidatePlane(luminance.Length, yStride, crop, 1);
        // Chroma addresses belong to the full frame, including odd crop origins.
        var uvRight = ((long)crop.X + crop.Width + 1) / 2 * 2;
        var uvBottom = ((long)crop.Y + crop.Height + 1) / 2;
        if (uvStride < uvRight || chroma.Length < (uvBottom - 1) * uvStride + uvRight)
            throw new ArgumentException("NV12 chroma plane does not cover the crop.");
        var mask = new byte[checked(crop.Width * crop.Height)];
        for (var y = 0; y < crop.Height; y++)
        for (var x = 0; x < crop.Width; x++)
        {
            var uv = (crop.Y + y) / 2 * uvStride + ((crop.X + x) / 2 * 2);
            var luma = (luminance[(crop.Y + y) * yStride + crop.X + x] - 16) * (255.0 / 219);
            var u = chroma[uv] - 128;
            var v = chroma[uv + 1] - 128;
            // BT.709 limited range, as emitted by the SDR capture converter.
            var r = Math.Clamp(luma + 1.792741 * v, 0, 255);
            var g = Math.Clamp(luma - 0.213249 * u - 0.532909 * v, 0, 255);
            var b = Math.Clamp(luma + 2.112402 * u, 0, 255);
            mask[y * crop.Width + x] = Select(r, g, b, x, crop.Width);
        }
        return new(crop.Width, crop.Height, mask);
    }

    private static byte Select(double r, double g, double b, int x, int width)
    {
        var skullArea = (long)x * 308 < (long)width * 120;
        // The skull turns gold before pink, and remains visible while fading.
        // These wider colour bounds apply only left of the multiplier.
        var pink = skullArea && r > 70 && r > 1.6 * g && r > b + 35 && b > 0.15 * r;
        var gold = skullArea && r > 140 && g > 130 && b < 0.45 * Math.Min(r, g) && Math.Abs(r - g) < 80;
        var yellow = r > 140 && g > 130 && b < 0.45 * Math.Min(r, g) && Math.Abs(r - g) < 35;
        return pink || gold || yellow ? (byte)255 : (byte)0;
    }

    private static void ValidatePlane(int length, int stride, PixelRegion crop, int bytesPerPixel)
    {
        var right = ((long)crop.X + crop.Width) * bytesPerPixel;
        var bottom = (long)crop.Y + crop.Height;
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            stride < right || length < (bottom - 1) * stride + right)
            throw new ArgumentException("Image plane does not cover the crop.");
    }
}
