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
