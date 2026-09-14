#if !CLYPDAT_LINUX
using Windows.Graphics.Imaging;
using Windows.Storage;
#endif

namespace ClypDat.App.Services;

/// <summary>
/// Reads a template PNG into the same greyscale form the capture pipeline
/// produces, so a reference crop and a live crop are directly comparable.
/// </summary>
public static class GrayPng
{
    public static GrayDetectorImage Read(string path) => ReadAsync(path).GetAwaiter().GetResult();

    public static async Task<GrayDetectorImage> ReadAsync(string path)
    {
#if CLYPDAT_LINUX
        using var bitmap = SkiaSharp.SKBitmap.Decode(path) ?? throw new InvalidDataException("Invalid template image.");
        var gray = new byte[checked(bitmap.Width * bitmap.Height)];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            gray[y * bitmap.Width + x] = (byte)((pixel.Red * 299 + pixel.Green * 587 + pixel.Blue * 114) / 1000);
        }
        return new GrayDetectorImage(bitmap.Width, bitmap.Height, gray);
#else
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

        var gray = new byte[width * height];
        for (var index = 0; index < gray.Length; index++)
        {
            var offset = index * 4;
            // Rec. 601 luma, matching what the capture path hands the detector.
            gray[index] = (byte)((bgra[offset + 2] * 299 + bgra[offset + 1] * 587 + bgra[offset] * 114) / 1000);
        }
        return new GrayDetectorImage(width, height, gray);
#endif
    }
}
