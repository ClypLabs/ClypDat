namespace ClypDat.App.Services;

// NV12 shares each chroma sample across a 2x2 block. Keep both the content
// bounds and their origin even so padding never shares chroma with gameplay.
internal readonly record struct CaptureAspectFit(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static CaptureAspectFit Create(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputWidth, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputHeight, 2);
        var canvasWidth = outputWidth & ~1;
        var canvasHeight = outputHeight & ~1;
        var scale = Math.Min((double)canvasWidth / sourceWidth, (double)canvasHeight / sourceHeight);
        var width = Math.Clamp((int)(sourceWidth * scale) & ~1, 2, canvasWidth);
        var height = Math.Clamp((int)(sourceHeight * scale) & ~1, 2, canvasHeight);
        return new(((canvasWidth - width) / 2) & ~1, ((canvasHeight - height) / 2) & ~1, width, height);
    }

    public (int X, int Y) MapCursor(int x, int y, int sourceWidth, int sourceHeight) =>
        x < 0 || y < 0 || x >= sourceWidth || y >= sourceHeight
            ? (int.MinValue, int.MinValue)
            : (X + (int)((long)x * Width / sourceWidth), Y + (int)((long)y * Height / sourceHeight));
}
