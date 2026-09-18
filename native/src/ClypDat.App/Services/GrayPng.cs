using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ClypDat.App.Services;

/// <summary>
/// Reads template and fixture PNGs, preserving the historical grayscale OCR
/// input alongside an optional Helldivers colour mask.
/// </summary>
public static class GrayPng
{
    public static GrayDetectorImage Read(string path) => ReadAsync(path).GetAwaiter().GetResult();

    public static async Task<GrayDetectorImage> ReadAsync(string path)
    {
        var (width, height, bgra) = await ReadBgraAsync(path);
        return ToGray(width, height, bgra);
    }

    public static async Task<(GrayDetectorImage Gray, GrayDetectorImage Mask)> ReadCounterAsync(
        string path, NormalizedRegion? region = null)
    {
        var (width, height, bgra) = await ReadBgraAsync(path);
        var crop = region?.ToPixelRect(width, height) ?? new PixelRegion(0, 0, width, height);
        var gray = ToGray(width, height, bgra);
        return (region is { } area ? GrayTemplateMatcher.Crop(gray, area) : gray,
            Helldivers2CounterMask.FromBgra(bgra, width * 4, crop));
    }

    private static async Task<(int Width, int Height, byte[] Pixels)> ReadBgraAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        if (bgra.Length != width * height * 4) throw new InvalidDataException("Template PNG has an unexpected stride.");
        return (width, height, bgra);
    }

    private static GrayDetectorImage ToGray(int width, int height, byte[] bgra)
    {
        var gray = new byte[width * height];
        for (var index = 0; index < gray.Length; index++)
        {
            var offset = index * 4;
            // Preserve the established Rec. 601 grayscale fixture/template input.
            gray[index] = (byte)((bgra[offset + 2] * 299 + bgra[offset + 1] * 587 + bgra[offset] * 114) / 1000);
        }
        return new GrayDetectorImage(width, height, gray);
    }
}
