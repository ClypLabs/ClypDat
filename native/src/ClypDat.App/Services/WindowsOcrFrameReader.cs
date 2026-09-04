using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ClypDat.App.Services;

public sealed record OcrWordObservation(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public sealed class WindowsOcrFrameReader
{
    private readonly OcrEngine _engine;

    public WindowsOcrFrameReader()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new NotSupportedException("Windows English OCR is unavailable.");
    }

    public Task<IReadOnlyList<OcrWordObservation>> ReadAsync(string imagePath) => ReadAsync(imagePath, null);

    public async Task<string> ReadTextAsync(GrayDetectorImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height)
            throw new InvalidDataException("OCR grayscale image dimensions are invalid.");
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            image.Pixels.AsBuffer(), BitmapPixelFormat.Gray8, image.Width, image.Height, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bitmap);
        return string.Join(' ', result.Lines.Select(line => line.Text));
    }

    public async Task<IReadOnlyList<OcrWordObservation>> ReadAsync(string imagePath, NormalizedRegion? region)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        SoftwareBitmap bitmap;
        if (region is { } crop)
        {
            var rect = crop.ToPixelRect((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds
                {
                    X = (uint)rect.X,
                    Y = (uint)rect.Y,
                    Width = (uint)rect.Width,
                    Height = (uint)rect.Height
                }
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        }
        using (bitmap)
        {
            if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension)
                throw new InvalidDataException($"OCR image exceeds {OcrEngine.MaxImageDimension}px maximum dimension.");

            var result = await _engine.RecognizeAsync(bitmap);
            return result.Lines
                .SelectMany(line => line.Words)
                .Select(word => new OcrWordObservation(
                    word.Text,
                    word.BoundingRect.X / bitmap.PixelWidth,
                    word.BoundingRect.Y / bitmap.PixelHeight,
                    word.BoundingRect.Width / bitmap.PixelWidth,
                    word.BoundingRect.Height / bitmap.PixelHeight))
                .ToArray();
        }
    }
}
