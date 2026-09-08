using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        var artifactDirectory = Environment.GetEnvironmentVariable("CLYPDAT_SPOTIFY_RENDER_CHECK");
        if (!string.IsNullOrWhiteSpace(artifactDirectory)) Directory.CreateDirectory(artifactDirectory);
        foreach (var position in new[] { "Top Left", "Top Right", "Center Left", "Center Right", "Bottom Left", "Bottom Right" })
        {
            using var renderer = new SpotifyCardFrames(1920, 1080, position, font);
            Assert.Equal(406, renderer.Width); Assert.Equal(140, renderer.Height);
            renderer.Render(card, 0); renderer.CopyStraightPixels();
            var initial = renderer.Pixels.ToArray();
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
                renderer.Bitmap.Save(Path.Combine(artifactDirectory, position.Replace(' ', '-') + ".png"), PngBitmapEncoderOptions.Default);
            var artworkX = position.EndsWith("Right") ? 288 : 22;
            var artPixel = (40 * renderer.Width + artworkX) * 4;
            Assert.Equal(initial[artPixel], initial[artPixel + 1]);
            Assert.Equal(initial[artPixel], initial[artPixel + 2]);
            Assert.Equal(255, initial[artPixel + 3]);
            Assert.Equal(0, initial[3]);
            Assert.Equal(0, initial[(renderer.Width - 1) * 4 + 3]);
            Assert.Equal(0, initial[((renderer.Height - 1) * renderer.Width) * 4 + 3]);
            Assert.Equal(0, initial[^1]);
            var insetPixel = (7 * renderer.Width + artworkX + 32) * 4;
            Assert.InRange(initial[insetPixel + 3], 1, 254);
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
        MetadataRowsAnimate(font);
        TimerDigitsKeepProgressRailFixed(font);
        AnimationAndComposition(font);
    }

    private static void MetadataRowsAnimate(FontFamily font)
    {
        var shortCard = new SpotifyCard("Short title", "Artist", "Album", TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(42), null);
        const string longText = "An overflowing metadata line with a different beginning and ending that must remain readable";
        var rows = new[] { (Y: 14, Height: 36), (Y: 50, Height: 27), (Y: 77, Height: 26) };
        foreach (var position in new[] { "Top Left", "Top Right" })
        {
            using var renderer = new SpotifyCardFrames(1920, 1080, position, font);
            var left = position.EndsWith("Right") ? 14 : 140;
            for (var row = 0; row < rows.Length; row++)
            {
                var card = row switch
                {
                    0 => shortCard with { Track = longText },
                    1 => shortCard with { Artist = longText },
                    _ => shortCard with { Album = longText }
                };
                renderer.Render(card, 0); renderer.CopyStraightPixels();
                var initial = renderer.Pixels.ToArray();
                renderer.Render(card, .5); renderer.CopyStraightPixels();
                Assert.Equal(initial, renderer.Pixels);
                renderer.Render(card, 2); renderer.CopyStraightPixels();
                var (top, height) = rows[row];
                Assert.False(RegionEqual(initial, renderer.Pixels, renderer.Width, left, top, 252, height),
                    $"Long metadata row {row} did not move for {position}.");
                for (var y = 0; y < renderer.Height; y++)
                {
                    if (y < top || y >= top + height)
                        Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, 0, y, renderer.Width, 1),
                            $"Animating row {row} changed another row at y={y} for {position}.");
                    else
                    {
                        Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, 0, y, left, 1),
                            $"Metadata escaped its left clip for {position}.");
                        Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 252, y, renderer.Width - left - 252, 1),
                            $"Metadata escaped its right clip for {position}.");
                    }
                }
                renderer.Render(card, 0); renderer.CopyStraightPixels();
                Assert.Equal(initial, renderer.Pixels);
            }
        }
    }

    private static bool RegionEqual(byte[] first, byte[] second, int width, int x, int y, int regionWidth, int regionHeight)
    {
        for (var row = y; row < y + regionHeight; row++)
        {
            var start = (row * width + x) * 4;
            if (!first.AsSpan(start, regionWidth * 4).SequenceEqual(second.AsSpan(start, regionWidth * 4))) return false;
        }
        return true;
    }

    private static void TimerDigitsKeepProgressRailFixed(FontFamily font)
    {
        foreach (var position in new[] { "Top Left", "Top Right" })
        {
            using var renderer = new SpotifyCardFrames(1920, 1080, position, font);
            var left = position.EndsWith("Right") ? 14 : 140;
            foreach (var (before, after) in new[] { (599, 600), (71, 488) })
            {
                // Keep playback halfway through while both timers change digit widths.
                var initialCard = new SpotifyCard("Title", "Artist", "Album", TimeSpan.FromSeconds(before * 2), TimeSpan.FromSeconds(before), null);
                renderer.Render(initialCard, 0); renderer.CopyStraightPixels();
                var initial = renderer.Pixels.ToArray();
                renderer.Render(initialCard with { Progress = TimeSpan.FromSeconds(after), Length = TimeSpan.FromSeconds(after * 2) }, 0);
                renderer.CopyStraightPixels();
                Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 48, 116, 156, 8),
                    $"Timer digits changed the progress rail for {position}: {before}s to {after}s.");
                Assert.False(RegionEqual(initial, renderer.Pixels, renderer.Width, left, 111, 48, 16),
                    "Elapsed timer did not update.");
                Assert.False(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 204, 111, 48, 16),
                    "Duration timer did not update.");

                renderer.Render(initialCard with { Progress = null }, 0); renderer.CopyStraightPixels();
                Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 48, 116, 8, 8));
                Assert.True(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 196, 116, 8, 8));
                Assert.False(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 56, 119, 1, 1),
                    "Progress rail left endpoint is missing.");
                Assert.False(RegionEqual(initial, renderer.Pixels, renderer.Width, left + 195, 119, 1, 1),
                    "Progress rail right endpoint is missing.");
            }
        }
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
            var firstOffset = (25 * 640 + 612) * 3;
            var lastOffset = 29 * 640 * 360 * 3 + firstOffset;
            Assert.True(Math.Abs(pixels[firstOffset] - pixels[firstOffset + 1]) < 12); // inset gray artwork on the right
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
