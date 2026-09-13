using System.Diagnostics;
using Avalonia;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class TimedEffectEditingTests
{
    private static readonly TimedVideoEffect Box = new() { X = .2, Y = .2, Width = .4, Height = .2, FontSize = 60 };

    [Fact]
    public void MoveStaysInsideFrameAndSnapsCentre()
    {
        var (clamped, _) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.Move, 2, -2, text: false, 0, 0);
        Assert.Equal(1 - Box.Width, clamped.X, 6);
        Assert.Equal(0, clamped.Y, 6);

        // Box centre at .4; a .09 move lands the centre .01 from the frame centre.
        var (snapped, guides) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.Move, .09, 0, text: false, .02, .02);
        Assert.Equal(.3, snapped.X, 6);
        Assert.Equal(.5, guides.X);
        Assert.Null(guides.Y);
    }

    [Fact]
    public void EdgeResizeKeepsMinimumSizeAndOppositeEdge()
    {
        var (shrunk, _) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.East, -1, 0, text: false, 0, 0);
        Assert.Equal(Box.X, shrunk.X, 6);
        Assert.Equal(TimedEffectManipulation.MinimumSize, shrunk.Width, 6);

        var (grown, _) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.NorthWest, -1, -1, text: false, 0, 0);
        Assert.Equal(0, grown.X, 6);
        Assert.Equal(0, grown.Y, 6);
        Assert.Equal(Box.X + Box.Width, grown.X + grown.Width, 6);
        Assert.Equal(Box.FontSize, grown.FontSize);
    }

    [Fact]
    public void TextCornerScalesFontAboutOppositeCorner()
    {
        var (scaled, _) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.SouthEast, .2, .1, text: true, 0, 0);
        Assert.Equal(Box.X, scaled.X, 6);
        Assert.Equal(Box.Y, scaled.Y, 6);
        Assert.Equal(Box.Width * 1.5, scaled.Width, 6);
        Assert.Equal(Box.Height * 1.5, scaled.Height, 6);
        Assert.Equal(90, scaled.FontSize, 6);

        var (limited, _) = TimedEffectManipulation.Apply(Box, TimedEffectHandle.NorthWest, 5, 5, text: true, 0, 0);
        Assert.Equal(8, limited.FontSize, 6);
        TimedEffectState.Validate([limited]);
        TimedEffectState.Validate([scaled]);
    }

    [Fact]
    public void TimeDragSnapsEitherEdgeAndClampsToClip()
    {
        var clip = new TimedVideoEffect { Start = 2, End = 5 };
        var moved = TimedEffectManipulation.ApplyTime(clip, 0, 2.95, 20, [8], .1);
        Assert.Equal(5, moved.Start, 6);
        Assert.Equal(8, moved.End, 6);

        var late = TimedEffectManipulation.ApplyTime(clip, 0, 30, 20, [], 0);
        Assert.Equal(17, late.Start, 6);
        Assert.Equal(20, late.End, 6);

        var trimmed = TimedEffectManipulation.ApplyTime(clip, -1, 10, 20, [], 0);
        Assert.Equal(clip.End - .1, trimmed.Start, 6);
        Assert.Equal(clip.End, trimmed.End);
    }

    [Fact]
    public void OverlappingClipsStackAndSequentialClipsShareARow()
    {
        TimedVideoEffect a = new() { Start = 0, End = 3 }, b = new() { Start = 1, End = 4 }, c = new() { Start = 3, End = 5 };
        var (rows, count) = TimedEffectState.PackRows([a, b, c]);
        Assert.Equal(2, count);
        Assert.Equal(0, rows[a.Id]);
        Assert.Equal(1, rows[b.Id]);
        Assert.Equal(0, rows[c.Id]);
        Assert.Equal(1, TimedEffectState.PackRows([]).Count);
    }

    [Fact]
    public void LiveBlurSmoothsEdgesAndLeavesFlatAreasAlone()
    {
        const int width = 32, height = 8;
        byte[] Frame(Func<int, byte> value)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var v = value(i % width);
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = 255;
            }
            return pixels;
        }
        var flat = Frame(_ => 90);
        TimedEffectPainter.Blur(flat, width, height, 3);
        Assert.All(flat, value => Assert.True(value is 90 or 255));

        var edge = Frame(x => (byte)(x < width / 2 ? 0 : 255));
        TimedEffectPainter.Blur(edge, width, height, 3);
        var row = Enumerable.Range(0, width).Select(x => (int)edge[(4 * width + x) * 4]).ToArray();
        Assert.True(row[15] > 0 && row[16] < 255, "the edge is softened");
        for (var x = 1; x < width; x++) Assert.True(row[x] >= row[x - 1], "values rise monotonically across the edge");
    }

    [Fact]
    public void BlurSnapshotsNeverPutLibVlcTextOverTheVideo()
    {
        Assert.Contains("--no-osd", PlaybackSession.LibVlcOptions);
        Assert.Contains("--no-snapshot-preview", PlaybackSession.LibVlcOptions);
    }

    [Fact]
    public void DownsampleAveragesBlocksIncludingPartialOnes()
    {
        // 3x1: values 0, 100, 200 at factor 2 → blocks {0,100} and {200}.
        var pixels = new byte[] { 0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255 };
        var (small, w, h) = TimedEffectPainter.Downsample(pixels, 3, 1, 2);
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(50, small[0]);
        Assert.Equal(200, small[4]);
        Assert.Same(pixels, TimedEffectPainter.Downsample(pixels, 3, 1, 1).Pixels);
    }

    [Fact]
    public void WorkingFactorKeepsTheBlurNearThreePixels()
    {
        // Strength 10 on a 1000-line frame: sigma ≈ 9.3 px.
        Assert.Equal(3, TimedEffectPainter.WorkingFactor(10 * 1000 / 1080.0));
        Assert.True(TimedEffectPainter.WorkingFactor(100 * 1000 / 1080.0) > 3);
        Assert.Equal(1, TimedEffectPainter.WorkingFactor(2));
    }

    [Theory]
    [InlineData("1:02.50", 62.5)]
    [InlineData("12.25", 12.25)]
    [InlineData("1:00:01", 3601)]
    public void TimecodesRoundTrip(string text, double seconds)
    {
        Assert.Equal(seconds, TimedEffectEditor.ParseTimecode(text)!.Value, 6);
        Assert.Equal(seconds, TimedEffectEditor.ParseTimecode(TimedEffectEditor.Timecode(seconds))!.Value, 2);
        Assert.Null(TimedEffectEditor.ParseTimecode("abc"));
    }
}
