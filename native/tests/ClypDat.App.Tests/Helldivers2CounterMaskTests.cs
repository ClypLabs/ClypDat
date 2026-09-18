using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Helldivers2CounterMaskTests
{
    [Fact]
    public void Nv12UsesIndependentPaddedStridesAndAbsoluteChromaCoordinates()
    {
        const int width = 8, height = 6, yStride = 13, uvStride = 17, bgraStride = 37;
        var yPlane = Enumerable.Repeat((byte)235, yStride * height).ToArray();
        var uvPlane = Enumerable.Repeat((byte)128, uvStride * height / 2).ToArray();
        var bgra = new byte[bgraStride * height];
        (byte R, byte G, byte B)[] colors = [(235, 220, 20), (230, 45, 90), (240, 240, 240), (60, 100, 200)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = colors[(x / 2 + y / 2) % colors.Length];
            var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            yPlane[y * yStride + x] = (byte)Math.Round(16 + luma * 219 / 255);
            var uv = y / 2 * uvStride + x / 2 * 2;
            uvPlane[uv] = (byte)Math.Round(128 + (b - luma) / 1.8556 * 224 / 255);
            uvPlane[uv + 1] = (byte)Math.Round(128 + (r - luma) / 1.5748 * 224 / 255);
            var offset = y * bgraStride + x * 4;
            bgra[offset] = b; bgra[offset + 1] = g; bgra[offset + 2] = r;
        }
        var originalY = yPlane.ToArray();
        var originalUv = uvPlane.ToArray();
        var crop = new PixelRegion(1, 1, 6, 4);
        var rgbMask = Helldivers2CounterMask.FromBgra(bgra, bgraStride, crop);
        var nv12Mask = Helldivers2CounterMask.FromNv12(yPlane, yStride, uvPlane, uvStride, crop);
        Assert.Contains((byte)255, rgbMask.Pixels);
        Assert.Contains((byte)0, rgbMask.Pixels);
        Assert.Equal(rgbMask.Pixels, nv12Mask.Pixels);
        Assert.Equal(originalY, yPlane);
        Assert.Equal(originalUv, uvPlane);
    }

    [Theory]
    [InlineData(230, 45, 90, true, false)] // Pink belongs only in the skull area.
    [InlineData(235, 220, 20, true, true)]
    [InlineData(255, 210, 0, true, false)] // Gold skull at the lower counts.
    [InlineData(100, 40, 45, true, false)] // Pink skull during fade.
    [InlineData(255, 255, 255, false, false)]
    [InlineData(240, 40, 0, false, false)] // Orange terrain/fire.
    [InlineData(40, 40, 40, false, false)]
    public void IsolatesHudColours(int r, int g, int b, bool skull, bool digits)
    {
        var bgra = new byte[308 * 4];
        for (var x = 0; x < 308; x++)
        {
            bgra[x * 4] = (byte)b; bgra[x * 4 + 1] = (byte)g; bgra[x * 4 + 2] = (byte)r;
        }
        var mask = Helldivers2CounterMask.FromBgra(bgra, bgra.Length, new(0, 0, 308, 1));
        Assert.Equal(skull ? 255 : 0, mask.Pixels[119]);
        Assert.Equal(digits ? 255 : 0, mask.Pixels[120]);
    }

    [Fact]
    public void RejectsPlanesThatCannotCoverCrop()
    {
        Assert.Throws<ArgumentException>(() => Helldivers2CounterMask.FromNv12(new byte[32], 8, new byte[7], 8, new(1, 1, 6, 3)));
        Assert.Throws<ArgumentException>(() => Helldivers2CounterMask.FromBgra(new byte[32], 4, new(1, 1, 2, 2)));
    }
}
