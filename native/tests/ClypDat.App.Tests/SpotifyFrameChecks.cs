using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// Called by the existing STA raster harness so Avalonia is initialized once.
internal static class SpotifyFrameChecks
{
    public static void Run()
    {
        var font = SpotifyOverlayCardRenderer.ResolveFont();
        var card = new SpotifyCard("A very long Spotify title that must slide across this narrow column", "Artist", "Album",
            TimeSpan.FromMinutes(123), TimeSpan.FromSeconds(42), null);
        foreach (var position in new[] { "Top Left", "Top Right", "Center Left", "Center Right", "Bottom Left", "Bottom Right" })
        {
            using var renderer = new SpotifyCardFrames(1920, 1080, position, font);
            Assert.Equal(406, renderer.Width); Assert.Equal(140, renderer.Height);
            renderer.Render(card, 0); renderer.CopyStraightPixels();
            var initial = renderer.Pixels.ToArray();
            var artworkX = position.EndsWith("Right") ? 400 : 0;
            var artPixel = (50 * renderer.Width + artworkX) * 4;
            Assert.Equal(42, initial[artPixel]); Assert.Equal(255, initial[artPixel + 3]);
            renderer.Render(card, .5); renderer.CopyStraightPixels();
            Assert.Equal(initial, renderer.Pixels);
            renderer.Render(card, 2); renderer.CopyStraightPixels();
            Assert.False(initial.SequenceEqual(renderer.Pixels));
            var graph = ClipRenderFilters.ComposeWithAnimation(null, position, "[0:v:0]", "[out]");
            Assert.Contains(position.EndsWith("Right") ? "overlay=main_w-overlay_w:" : "overlay=0:", graph);
            renderer.Render(null, 0); renderer.CopyStraightPixels();
            Assert.All(renderer.Pixels, pixel => Assert.Equal(0, pixel));
        }
        using (var narrow = new SpotifyCardFrames(60, 100, "Bottom Right", font))
        { Assert.True(narrow.Width <= 60); Assert.True(narrow.Height <= 100); }
        using (var normal = new SpotifyCardFrames(1920, 1080, "Top Left", font))
        using (var custom = new SpotifyCardFrames(1920, 1080, "Top Left", new FontFamily("Courier New")))
        {
            normal.Render(card with { Track = "Short" }, 0); normal.CopyStraightPixels();
            var still = normal.Pixels.ToArray();
            normal.Render(card with { Track = "Short" }, 99); normal.CopyStraightPixels();
            Assert.Equal(still, normal.Pixels);
            custom.Render(card with { Track = "Short" }, 0); custom.CopyStraightPixels();
            Assert.False(still.SequenceEqual(custom.Pixels));
        }
        AnimationAndComposition(font);
    }

    private static void AnimationAndComposition(FontFamily font)
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var timeline = new SpotifyTimeline(1, new[] {
            new SpotifyTimelineSample(0, "a", "AAAA", "artist A", "album A", 10000, 1000, true, null, true),
            new SpotifyTimelineSample(.5, "b", "BBBB", "artist B", "album B", 50000, 4000, false, null, true),
            new SpotifyTimelineSample(.8, null, null, null, null, null, null, false, null, false) });
        var spec = new SpotifyRenderSpec(timeline, null, 640, 360, 0, 1, 1, "Top Right", font);
        using var animation = Pump(SpotifyOverlayAnimation.PrepareAsync(spec, CancellationToken.None));
        Assert.True(File.Exists(animation.Path));
        var raw = ReadPixels(animation.Path, "-f", "rawvideo", "-pix_fmt", "bgra");
        var width = (int)Math.Ceiling(406 / 3.0); var height = (int)Math.Ceiling(140 / 3.0);
        var bytes = width * height * 4;
        Assert.Equal(bytes * 30, raw.Length);
        Assert.False(raw.AsSpan(0, bytes).SequenceEqual(raw.AsSpan(bytes * 18, bytes)));
        Assert.All(raw.Skip(bytes * 25), pixel => Assert.Equal(0, pixel));
        var folder = Path.Combine(Path.GetTempPath(), "Spotify ' Unicode 音 " + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "clip.mp4");
            Pump(SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "color=green:s=640x360:r=30:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-map", "0:v", "-map", "1:a", "-map", "1:a", "-map", "1:a", "-map", "1:a", "-t", "1", "-metadata", "title=Test Unicode 音",
                "-movflags", "+use_metadata_tags", "-c:v", "libx264", "-c:a", "aac", clip).ContinueWith(task => { task.GetAwaiter().GetResult(); return true; }));
            // Invalid hardware encoder deliberately exercises reuse on CPU fallback.
            Assert.Equal(SpotifyOverlayOutcome.Completed, SpotifyOverlayBurner.BurnAsync(clip, animation.Path, spec.Position,
                preferredCodec: new[] { "-c:v", "nonexistent_encoder" }).GetAwaiter().GetResult());
            var inspection = SpotifyOverlayBurner.InspectAsync(clip).GetAwaiter().GetResult();
            Assert.Equal(4, inspection.Audio.Length); Assert.True(inspection.Burned); Assert.InRange(inspection.Duration, .95, 1.05);
            var pixels = ReadPixels(clip, "-f", "rawvideo", "-pix_fmt", "rgb24");
            var firstOffset = (20 * 640 + 635) * 3;
            var lastOffset = 29 * 640 * 360 * 3 + firstOffset;
            Assert.True(Math.Abs(pixels[firstOffset] - pixels[firstOffset + 1]) < 12); // gray artwork at right edge
            Assert.True(pixels[lastOffset + 1] > pixels[lastOffset] + 60); // unavailable clears to green video
            Assert.Equal(SpotifyOverlayOutcome.Skipped, SpotifyOverlayBurner.BurnAsync(clip, animation.Path, spec.Position).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(folder, true); }

        var cancellation = new CancellationTokenSource();
        var task = SpotifyOverlayAnimation.PrepareAsync(spec with { Duration = 60 }, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Pump(task));
    }

    private static T Pump<T>(Task<T> task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(30))
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "Animation job did not stop.");
        return task.GetAwaiter().GetResult();
    }
    private static byte[] ReadPixels(string path, params string[] output)
    {
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "-v", "error", "-i", path, "-an" }.Concat(output).Append("pipe:1")) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        using var data = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(data);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return data.ToArray();
    }
}
