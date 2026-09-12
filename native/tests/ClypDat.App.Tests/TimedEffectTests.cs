using System.Diagnostics;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class TimedEffectTests
{
    [Fact]
    public void LegacySidecarHasEmptyEffects()
    {
        var edit = JsonSerializer.Deserialize<ClipEditSettings>("{\"TrimStartSeconds\":1}")!;
        Assert.Empty(edit.TextEffects); Assert.Empty(edit.BlurEffects);
    }

    [Fact]
    public void TrimRebasesBothLanesAndRetainsStylesAndIdentity()
    {
        var item = new TimedVideoEffect { Start = 2, End = 8, Text = "你好\n'quoted': [x], %", Bold = true };
        var edit = new ClipEditSettings { TrimStartSeconds = 4, TrimEndSeconds = 10, SpeedMultiplier = 2,
            TextEffects = [item, item with { Id = Guid.NewGuid(), Start = 0, End = 4 }], BlurEffects = [item], CropMode = "9:16" };
        var result = ClipEditSidecar.RebaseAfterTrim(edit);
        Assert.Equal(item with { Start = 0, End = 2 }, Assert.Single(result.TextEffects));
        Assert.Equal(Assert.Single(result.TextEffects), Assert.Single(result.BlurEffects));
        Assert.Equal("None", result.CropMode); Assert.Equal(1, result.SpeedMultiplier);
        Assert.Equal(0, result.TrimEndSeconds);
    }

    [Fact]
    public void InvalidAndOversizedEditsCannotBeSerialized()
    {
        Assert.Throws<InvalidDataException>(() => TimedEffectState.Validate([new() { X = double.NaN }]));
        Assert.Throws<InvalidDataException>(() => TimedEffectState.Validate([new() { Start = 3, End = 3 }]));
        Assert.Throws<InvalidDataException>(() => TimedEffectState.Validate([new() { Colour = "[filter]" }]));
        Assert.Throws<InvalidDataException>(() => ClipEditSidecar.SerializeValidated(new() { TextEffects = [new() { Text = new string('x', 2001) }] }));
        Assert.Throws<InvalidDataException>(() => ClipEditSidecar.SerializeValidated(new() { Description = new string('x', 66000) }));
    }

    [Fact]
    public void BlurComposesAfterUnderlyingLayersWithExclusiveEnd()
    {
        var graph = ClipRenderFilters.ComposeWithOverlays("setpts=PTS/2",
            [new(new(0, 0, 20, 20), null, true, "[1:v:0]"), new(new(0, 0, 20, 20), "gte(t,1)*lt(t,2)", false, "", 10)], "[0:v:0]", "[out]");
        Assert.True(graph.IndexOf("overlay=", StringComparison.Ordinal) < graph.IndexOf("gblur=", StringComparison.Ordinal));
        Assert.Contains("gte(t,1)*lt(t,2)", graph);
    }

    [Fact]
    public void ActualBlurPixelsChangeOnlyInsideActiveTimeAndRegion()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-effect-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var graph = ClipRenderFilters.ComposeWithOverlays(null,
                [new(new(16, 0, 32, 64), "gte(t,0.5)*lt(t,1)", false, "", 5)], "[0:v:0]", "[out]");
            var output = Path.Combine(root, "pixels.rgb");
            Run("-y", "-f", "lavfi", "-i", "color=black:s=64x64:r=4:d=1.5,drawbox=x=32:y=0:w=32:h=64:color=white:t=fill",
                "-filter_complex", graph, "-map", "[out]", "-pix_fmt", "rgb24", "-f", "rawvideo", output);
            var pixels = File.ReadAllBytes(output);
            var frameSize = 64 * 64 * 3;
            Assert.Equal(6 * frameSize, pixels.Length);
            int Pixel(int frame, int x) => pixels[frame * frameSize + (32 * 64 + x) * 3];
            Assert.True(Pixel(0, 30) < 10);
            Assert.True(Pixel(2, 30) > 30);
            Assert.True(Pixel(4, 30) < 10);
            Assert.True(Pixel(2, 4) < 10);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExportBlurMixesInThePictureAroundTheBox()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-effect-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The box covers only black; white starts right after its right edge.
            var graph = ClipRenderFilters.ComposeWithOverlays(null,
                [new(new(0, 0, 32, 64), "gte(t,0)", false, "", 5, FrameWidth: 64, FrameHeight: 64)], "[0:v:0]", "[out]");
            var output = Path.Combine(root, "pixels.rgb");
            Run("-y", "-f", "lavfi", "-i", "color=black:s=64x64:r=4:d=0.25,drawbox=x=32:y=0:w=32:h=64:color=white:t=fill",
                "-filter_complex", graph, "-map", "[out]", "-pix_fmt", "rgb24", "-f", "rawvideo", output);
            var pixels = File.ReadAllBytes(output);
            int Pixel(int x) => pixels[(32 * 64 + x) * 3];
            Assert.True(Pixel(31) > 30, "the box edge should pick up the white beside it");
            Assert.True(Pixel(2) < 10);
            Assert.True(Pixel(40) > 245, "outside the box stays untouched");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ShapedExportBlurOnlyReplacesPixelsInsideTheMask()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-effect-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Box x16-48 straddles a black/white edge at x32; the mask is the
            // ellipse inscribed in the box, a single frame like the real PNG.
            var graph = ClipRenderFilters.ComposeWithOverlays(null,
                [new(new(16, 0, 32, 64), "gte(t,0)", false, "[1:v:0]", 10, FrameWidth: 64, FrameHeight: 64)], "[0:v:0]", "[out]");
            var output = Path.Combine(root, "pixels.rgb");
            Run("-y", "-f", "lavfi", "-i", "color=black:s=64x64:r=4:d=0.5,drawbox=x=32:y=0:w=32:h=64:color=white:t=fill",
                "-f", "lavfi", "-i", "color=black:s=32x64:d=0.04,format=gray,geq=lum='255*lte(pow((X-15.5)/16\\,2)+pow((Y-31.5)/32\\,2)\\,1)'",
                "-filter_complex", graph, "-map", "[out]", "-pix_fmt", "rgb24", "-f", "rawvideo", output);
            var pixels = File.ReadAllBytes(output);
            const int frameSize = 64 * 64 * 3;
            Assert.Equal(2 * frameSize, pixels.Length);
            int Pixel(int frame, int x, int y) => pixels[frame * frameSize + (y * 64 + x) * 3];
            foreach (var frame in new[] { 0, 1 })
            {
                Assert.True(Pixel(frame, 33, 32) < 235, "the middle of the ellipse is blurred");
                Assert.True(Pixel(frame, 40, 2) > 250, "the box corner outside the ellipse is untouched");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void UnicodeCaptionRasterFeedsFfmpegAndStopsAtBaseDuration()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var render = TimedEffectRender.PrepareAsync([new() { Text = "Hello 世界\n' : [%]", Start = .5, End = 1, FontSize = 150, X = 0, Y = 0, Width = 1, Height = 1 }], [], 0, 1.5, 1, 320, 180, CancellationToken.None).GetAwaiter().GetResult();
            var image = Assert.Single(render.Text);
            Assert.True(new FileInfo(image.Path).Length > 100);
            var output = Path.ChangeExtension(image.Path, ".rgb");
            try
            {
                var graph = ClipRenderFilters.ComposeWithOverlays(null, [new(image.Bounds, image.Enable, true, "[1:v:0]", StillImage: true)], "[0:v:0]", "[out]");
                Run("-y", "-f", "lavfi", "-i", "color=black:s=320x180:r=4:d=1.5", "-loop", "1", "-i", image.Path,
                    "-filter_complex", graph, "-map", "[out]", "-pix_fmt", "rgb24", "-f", "rawvideo", output);
                var pixels = File.ReadAllBytes(output);
                const int frame = 320 * 180 * 3;
                Assert.Equal(6 * frame, pixels.Length);
                Assert.DoesNotContain(pixels.Take(frame), p => p > 20);
                Assert.Contains(pixels.Skip(2 * frame).Take(frame), p => p > 100);
                Assert.DoesNotContain(pixels.Skip(4 * frame).Take(frame), p => p > 20);
            }
            finally { File.Delete(output); }
        }, TimeSpan.FromSeconds(45), "timed text raster");
    }

    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void ShapedBlurGetsAMaskAndRectangleDoesNot()
    {
        AvaloniaTestThread.Run(() =>
        {
            var blurs = new TimedVideoEffect[] { new() { Shape = "Ellipse", X = 0, Y = 0, Width = .5, Height = .5 }, new() { X = .5, Y = .5, Width = .5, Height = .5 } };
            using var render = TimedEffectRender.PrepareAsync([], blurs, 0, 3, 1, 40, 20, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Null(render.Blur[1].Mask);
            var mask = render.Blur[0].Mask!;
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(mask);
            Assert.Equal(new Avalonia.PixelSize(20, 10), bitmap.PixelSize);
            var pixels = new byte[20 * 10 * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, 20, 10), handle.AddrOfPinnedObject(), pixels.Length, 20 * 4); }
            finally { handle.Free(); }
            Assert.True(pixels[(5 * 20 + 10) * 4] > 240, "centre is inside the ellipse");
            Assert.True(pixels[0] < 15, "corner is outside it");
        }, TimeSpan.FromSeconds(45), "blur shape mask");
    }

    private static void Run(params string[] args)
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("FFmpeg test exceeded 30 seconds."); }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
    }
}
