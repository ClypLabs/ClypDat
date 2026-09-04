using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ClypDat.App.Services;

public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height)
{
    public PixelRegion ToPixelRect(int imageWidth, int imageHeight)
    {
        var x = Math.Clamp((int)Math.Round(X * imageWidth), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Round(Y * imageHeight), 0, imageHeight - 1);
        var width = Math.Clamp((int)Math.Round(Width * imageWidth), 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Round(Height * imageHeight), 1, imageHeight - y);
        return new PixelRegion(x, y, width, height);
    }
}

public readonly record struct PixelRegion(int X, int Y, int Width, int Height);

/// <summary>Brightness-invariant normalized correlation for one fixed HUD region.</summary>
public sealed class FixedRegionTemplateMatcher
{
    private readonly NormalizedRegion _region;
    private readonly double[] _centeredTemplate;
    private readonly double _templateMagnitude;

    private FixedRegionTemplateMatcher(NormalizedRegion region, byte[] template)
    {
        _region = region;
        var mean = template.Average(value => (double)value);
        _centeredTemplate = template.Select(value => value - mean).ToArray();
        _templateMagnitude = Math.Sqrt(_centeredTemplate.Sum(value => value * value));
    }

    public static async Task<FixedRegionTemplateMatcher> FromImageAsync(string imagePath, NormalizedRegion region) =>
        new(region, await ReadGrayAsync(imagePath, region));

    public async Task<double> ScoreAsync(string imagePath)
    {
        var candidate = await ReadGrayAsync(imagePath, _region);
        if (candidate.Length != _centeredTemplate.Length) return 0;
        var mean = candidate.Average(value => (double)value);
        double dot = 0;
        double magnitudeSquared = 0;
        for (var index = 0; index < candidate.Length; index++)
        {
            var centered = candidate[index] - mean;
            dot += centered * _centeredTemplate[index];
            magnitudeSquared += centered * centered;
        }
        var denominator = _templateMagnitude * Math.Sqrt(magnitudeSquared);
        return denominator <= double.Epsilon ? 0 : Math.Clamp(dot / denominator, -1, 1);
    }

    private static async Task<byte[]> ReadGrayAsync(string imagePath, NormalizedRegion region)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var rect = region.ToPixelRect((int)decoder.PixelWidth, (int)decoder.PixelHeight);
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
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();
        if (bgra.Length != rect.Width * rect.Height * 4)
        {
            throw new InvalidDataException("Decoded template region has an unexpected stride.");
        }

        var gray = new byte[rect.Width * rect.Height];
        for (int source = 0, destination = 0; destination < gray.Length; source += 4, destination++)
            gray[destination] = (byte)((bgra[source] * 29 + bgra[source + 1] * 150 + bgra[source + 2] * 77) >> 8);
        return gray;
    }
}
