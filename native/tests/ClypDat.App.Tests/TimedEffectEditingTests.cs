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
    public void DecodeAreaCoversPaddedBlursOnAGridAtDisplayResolution()
    {
        var crop = new ClipRenderFilters.CropRect(0, 0, 2560, 1440);
        var blur = new TimedVideoEffect { X = .4, Y = .4, Width = .2, Height = .1, Strength = 20 };
        var area = TimedEffectFrameSource.PlanArea([blur], crop, 1280, 720)!.Value;
        Assert.Equal(new ClipRenderFilters.CropRect(800, 450, 960, 360), area.Source);
        Assert.Equal((480, 180), (area.Width, area.Height));
        Assert.Equal(new Rect(.3125, .3125, .375, .25), area.Area);
        Assert.Null(TimedEffectFrameSource.PlanArea([], crop, 1280, 720));

        // Nudging the box a little keeps the same decode.
        Assert.Equal(area, TimedEffectFrameSource.PlanArea([blur with { X = .405 }], crop, 1280, 720));

        var whole = TimedEffectFrameSource.PlanArea([new TimedVideoEffect { X = 0, Y = 0, Width = 1, Height = 1 }], crop, 2560, 1440)!.Value;
        Assert.Equal(crop, whole.Source);
        Assert.True(whole.Width * whole.Height * 4 <= TimedEffectFrameSource.MaximumFrameBytes);
        Assert.Equal(0, whole.Width % 2);
        Assert.Equal(0, whole.Height % 2);
    }

    [Fact]
    public void ExtractRepeatsTheFrameEdgeBeyondIt()
    {
        // 4x2 frame whose blue channel is the column index.
        var pixels = new byte[4 * 2 * 4];
        for (var i = 0; i < 8; i++) { pixels[i * 4] = (byte)(i % 4); pixels[i * 4 + 3] = 255; }
        var frame = new TimedEffectFrameSource.Frame(pixels, 4, 2, 1, new Rect(0, 0, 1, 1));

        var (left, width, height, covered) = TimedEffectFrameSource.Extract(frame, new Rect(-.5, 0, 1, 1));
        Assert.Equal((4, 2), (width, height));
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, Enumerable.Range(0, 4).Select(x => left[x * 4]).ToArray());
        Assert.Equal(new Rect(-.5, 0, 1, 1), covered);

        var right = TimedEffectFrameSource.Extract(frame, new Rect(.75, 0, .5, 1));
        Assert.Equal(new byte[] { 3, 3 }, Enumerable.Range(0, 2).Select(x => right.Pixels[x * 4]).ToArray());

        var half = new TimedEffectFrameSource.Frame(pixels, 4, 2, 2, new Rect(.5, 0, .5, 1));
        var inner = TimedEffectFrameSource.Extract(half, new Rect(.625, 0, .25, 1));
        Assert.Equal(new byte[] { 1, 2 }, Enumerable.Range(0, 2).Select(x => inner.Pixels[x * 4]).ToArray());
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

    [Fact]
    public void FrameSourceIndexesOneSecondChunksAtSixtyFpsNeverAhead()
    {
        Assert.Equal(1, TimedEffectFrameSource.ChunkIndex(1.99));
        Assert.Equal(2, TimedEffectFrameSource.ChunkIndex(2));
        Assert.Equal(30, TimedEffectFrameSource.FrameIndex(2.5, 2, 60));
        // 2.532s is still frame 31 (2.5167s); rounding would jump ahead to frame 32 (2.5333s).
        Assert.Equal(31, TimedEffectFrameSource.FrameIndex(2.532, 2, 60));
        Assert.Equal(59, TimedEffectFrameSource.FrameIndex(9, 2, 60));
    }

    [Fact]
    public void SnapshotYieldsTheScaledCropOutputAtDisplayResolution()
    {
        // 8x4 snapshot of a 16x8 source: left half black, right half white.
        const int width = 8, height = 4;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var value = (byte)(i % width < width / 2 ? 0 : 255);
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = value;
            bgra[i * 4 + 3] = 255;
        }
        var full = TimedEffectFrameSource.FromSnapshot(bgra, width, height, new(0, 0, 16, 8), 16, 8)!.Value;
        Assert.Equal((8, 4), (full.Width, full.Height));
        Assert.Equal(bgra, full.Pixels);
        Assert.Equal(new Rect(0, 0, 1, 1), full.Area);

        var right = TimedEffectFrameSource.FromSnapshot(bgra, width, height, new(8, 0, 8, 8), 16, 8)!.Value;
        Assert.Equal((4, 4), (right.Width, right.Height));
        Assert.All(right.Pixels, value => Assert.Equal(255, value));
        Assert.NotEqual(full.Id, right.Id);

        Assert.Equal(1280u, TimedEffectFrameSource.SnapshotWidth(1280, 2560, new(0, 0, 2560, 1440)));
        Assert.Equal(2560u, TimedEffectFrameSource.SnapshotWidth(1280, 2560, new(640, 0, 1280, 1440)));
        Assert.Equal(2560u, TimedEffectFrameSource.SnapshotWidth(4000, 2560, new(0, 0, 2560, 1440)));
    }

    [Fact]
    public void FrameSourceDecodesTheBlurAreaFromTheClip()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var path = Path.Combine(Path.GetTempPath(), $"clypdat-blur-source-{Guid.NewGuid():N}.mkv");
        try
        {
            using (var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } })
            {
                foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "color=white:s=64x64:r=30:d=3,drawbox=x=0:y=0:w=32:h=64:color=black:t=fill", "-c:v", "ffv1", path })
                    process.StartInfo.ArgumentList.Add(arg);
                process.Start();
                _ = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(30000));
                Assert.Equal(0, process.ExitCode);
            }
            using var source = new TimedEffectFrameSource();
            // Crop to the white half; a blur over all of it.
            var crop = new ClipRenderFilters.CropRect(32, 0, 32, 64);
            var area = TimedEffectFrameSource.PlanArea([new TimedVideoEffect { X = 0, Y = 0, Width = 1, Height = 1 }], crop, 32, 64)!.Value;
            Assert.Equal(crop, area.Source);
            TimedEffectFrameSource.Frame? frame = null;
            var clock = Stopwatch.StartNew();
            while (frame is null && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                frame = source.Request(path, area, 2.5, 3);
                if (frame is null) Thread.Sleep(50);
            }
            Assert.NotNull(frame);
            Assert.Equal((32, 64), (frame.Value.Width, frame.Value.Height));
            Assert.Equal(new Rect(0, 0, 1, 1), frame.Value.Area);
            Assert.True(frame.Value.Pixels[(32 * 32 + 16) * 4] > 200);
        }
        finally { File.Delete(path); }
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
