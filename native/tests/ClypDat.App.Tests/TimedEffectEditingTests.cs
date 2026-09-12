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
    public void BlurLeavesUniformRegionsAndPixelsOutsideAlone()
    {
        const int width = 32, height = 8;
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(x < width / 2 ? 0 : 255);
            for (var c = 0; c < 4; c++) frame[(y * width + x) * 4 + c] = c == 3 ? (byte)255 : value;
        }
        var uniform = TimedEffectPainter.BlurRegion(frame, width, height, new PixelRect(0, 0, 8, height), 3);
        Assert.Equal(8 * height * 4, uniform.Length);
        Assert.All(uniform.Where((_, i) => i % 4 != 3), value => Assert.Equal(0, value));

        var edge = TimedEffectPainter.BlurRegion(frame, width, height, new PixelRect(8, 0, 16, height), 3);
        int At(int x) => edge[(4 * 16 + x) * 4];
        Assert.True(At(7) > 0 && At(7) < 255);
        Assert.True(At(0) < At(7) && At(7) < At(15));
        Assert.Equal(0, frame[(4 * width + 15) * 4]);
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
        Assert.Equal((480, 270), TimedEffectFrameSource.DecodeSize(1920, 1080));
        Assert.Equal((152, 270), TimedEffectFrameSource.DecodeSize(1080, 1920));
        Assert.Equal((100, 50), TimedEffectFrameSource.DecodeSize(100, 50));
    }

    [Fact]
    public void SnapshotYieldsTheScaledCropOutput()
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

        var right = TimedEffectFrameSource.FromSnapshot(bgra, width, height, new(8, 0, 8, 8), 16, 8)!.Value;
        Assert.Equal((4, 4), (right.Width, right.Height));
        Assert.All(right.Pixels, value => Assert.Equal(255, value));
        Assert.NotEqual(full.Id, right.Id);

        Assert.Equal(480u, TimedEffectFrameSource.SnapshotWidth(2560, 1440, new(0, 0, 2560, 1440)));
        Assert.Equal(1920u, TimedEffectFrameSource.SnapshotWidth(2560, 1440, new(1875, 0, 810, 1440 / 6)));
    }

    [Fact]
    public void FrameSourceDecodesCroppedFramesFromTheClip()
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
            var crop = new ClipRenderFilters.CropRect(32, 0, 32, 64);
            TimedEffectFrameSource.Frame? frame = null;
            var clock = Stopwatch.StartNew();
            while (frame is null && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                frame = source.Request(path, crop, 2.5, 3);
                if (frame is null) Thread.Sleep(50);
            }
            Assert.NotNull(frame);
            Assert.Equal((32, 64), (frame.Value.Width, frame.Value.Height));
            // The crop keeps only the white half.
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
