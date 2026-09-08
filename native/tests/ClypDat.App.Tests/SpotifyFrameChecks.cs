using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

// Called by the existing STA raster harness so Avalonia is initialized once.
internal static class SpotifyFrameChecks
{
    public static void Run()
    {
        SpotifyOverlayLayerStateTests.OverlayLaneIsIndependentFromMediaStreams();
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
        CoverBackgroundsFollowArtwork(font);
        RoundedCornersStaySymmetric(font);
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

    private static void CoverBackgroundsFollowArtwork(FontFamily font)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Spotify palettes " + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var warmPath = Path.Combine(folder, "warm.png");
            var coolPath = Path.Combine(folder, "cool.png");
            SaveArtwork(warmPath, Color.FromRgb(238, 58, 32), Color.FromRgb(210, 35, 111));
            SaveArtwork(coolPath, Color.FromRgb(28, 83, 238), Color.FromRgb(29, 192, 219));
            var card = new SpotifyCard("Title", "Artist", "Album", TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(42), warmPath);
            using var still = new SpotifyCardFrames(1920, 1080, "Top Left", font, dynamicBackground: false);
            var warm = Frame(still, card, 0);
            Assert.Equal(warm, Frame(still, card, 9));
            var cool = Frame(still, card with { ArtPath = coolPath }, 9);
            Assert.False(RegionEqual(warm, cool, still.Width, 140, 98, 252, 8), "Cover colors did not change the background.");
            Assert.False(RegionEqual(warm, cool, still.Width, 22, 35, 20, 20), "Cover artwork did not change with its palette.");
            Assert.Equal(warm, Frame(still, card, 0));

            using var moving = new SpotifyCardFrames(1920, 1080, "Top Left", font, dynamicBackground: true);
            var start = Frame(moving, card, 0);
            var later = Frame(moving, card, 4);
            var artifactDirectory = Environment.GetEnvironmentVariable("CLYPDAT_SPOTIFY_RENDER_CHECK");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
                moving.Bitmap.Save(Path.Combine(artifactDirectory, "Cover-Gradient.png"), PngBitmapEncoderOptions.Default);
            Assert.False(RegionEqual(start, later, moving.Width, 140, 98, 252, 8), "Dynamic cover background did not animate.");
            Assert.True(RegionEqual(start, later, moving.Width, 22, 35, 20, 20), "Background animation changed the cover artwork.");
            Assert.Equal(start, Frame(moving, card, 0));
            Assert.Equal(later, Frame(moving, card, 4));
            using var replay = new SpotifyCardFrames(1920, 1080, "Top Left", font, dynamicBackground: true);
            Assert.Equal(later, Frame(replay, card, 4));
            var changed = Frame(moving, card with { ArtPath = coolPath }, 4);
            Assert.False(RegionEqual(later, changed, moving.Width, 140, 98, 252, 8), "Animated background retained the previous cover palette.");
            Assert.False(RegionEqual(later, changed, moving.Width, 22, 35, 20, 20), "Animated card retained the previous cover artwork.");

            var noArtwork = card with { ArtPath = null };
            var fallback = Frame(moving, noArtwork, 0);
            Assert.Equal(fallback, Frame(moving, noArtwork, 9));
            Assert.Equal(fallback, Frame(moving, noArtwork with { ArtPath = Path.Combine(folder, "missing.png") }, 9));
            Assert.Equal(fallback, Frame(still, noArtwork, 9));
            var backgroundPixel = (102 * moving.Width + 250) * 4;
            var channels = fallback.AsSpan(backgroundPixel, 3).ToArray();
            Assert.InRange(channels.Max() - channels.Min(), 0, 16);

            // Exercise the settings snapshot through the actual FFV1 pipeline.
            // Short text and fixed timers leave only the background free to move.
            FfmpegPathResolver.EnsureBundledFfmpeg();
            var spec = new SpotifyRenderSpec(null, card, 640, 360, 0, 1, 1, "Top Left", font, DynamicBackground: false);
            using var staticAnimation = Pump(SpotifyOverlayAnimation.PrepareAsync(spec, CancellationToken.None));
            var encodedStill = ReadPixels(staticAnimation.Path, "-f", "rawvideo", "-pix_fmt", "bgra");
            var frameBytes = encodedStill.Length / 30;
            Assert.True(encodedStill.AsSpan(0, frameBytes).SequenceEqual(encodedStill.AsSpan(29 * frameBytes, frameBytes)));
            using var dynamicAnimation = Pump(SpotifyOverlayAnimation.PrepareAsync(spec with { DynamicBackground = true }, CancellationToken.None));
            var encodedMoving = ReadPixels(dynamicAnimation.Path, "-f", "rawvideo", "-pix_fmt", "bgra");
            Assert.Equal(encodedStill.Length, encodedMoving.Length);
            Assert.True(encodedStill.AsSpan(0, frameBytes).SequenceEqual(encodedMoving.AsSpan(0, frameBytes)));
            Assert.False(encodedMoving.AsSpan(0, frameBytes).SequenceEqual(encodedMoving.AsSpan(29 * frameBytes, frameBytes)));
        }
        finally { Directory.Delete(folder, true); }

        static byte[] Frame(SpotifyCardFrames renderer, SpotifyCard card, double seconds)
        {
            renderer.Render(card, seconds);
            renderer.CopyStraightPixels();
            return renderer.Pixels.ToArray();
        }
        static void SaveArtwork(string path, Color primary, Color secondary)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var drawing = bitmap.CreateDrawingContext())
            {
                drawing.DrawRectangle(new SolidColorBrush(primary), null, new Rect(0, 0, 64, 64));
                drawing.DrawRectangle(new SolidColorBrush(secondary), null, new Rect(32, 0, 32, 64));
            }
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
    }

    private static void RoundedCornersStaySymmetric(FontFamily font)
    {
        var card = new SpotifyCard("", null, null, null, null, null);
        foreach (var (width, height) in new[] { (640, 360), (518, 291), (1280, 720), (1366, 768), (1920, 1080), (2560, 1440), (60, 100) })
        {
            using var renderer = new SpotifyCardFrames(width, height, "Top Left", font, dynamicBackground: false);
            renderer.Render(card, 0); renderer.CopyStraightPixels();
            var scaleX = renderer.Width / 406.0;
            var scaleY = renderer.Height / 140.0;
            var cornerWidth = (int)Math.Ceiling(22 * scaleX);
            var cornerHeight = (int)Math.Ceiling(22 * scaleY);
            // Raster antialias coverage differs between mirrored curves, even
            // at integer scale. Compare contour positions, not individual alpha.
            foreach (var threshold in new[] { 32, 64, 118, 128, 192, 224 })
            {
                for (var y = 0; y < cornerHeight; y++)
                    AssertContourClose(new[] {
                        Depth(cornerWidth, x => Alpha(x, y)),
                        Depth(cornerWidth, x => Alpha(renderer.Width - 1 - x, y)),
                        Depth(cornerWidth, x => Alpha(x, renderer.Height - 1 - y)),
                        Depth(cornerWidth, x => Alpha(renderer.Width - 1 - x, renderer.Height - 1 - y)) });
                for (var x = 0; x < cornerWidth; x++)
                    AssertContourClose(new[] {
                        Depth(cornerHeight, y => Alpha(x, y)),
                        Depth(cornerHeight, y => Alpha(renderer.Width - 1 - x, y)),
                        Depth(cornerHeight, y => Alpha(x, renderer.Height - 1 - y)),
                        Depth(cornerHeight, y => Alpha(renderer.Width - 1 - x, renderer.Height - 1 - y)) });

                int Depth(int limit, Func<int, byte> alpha)
                {
                    var depth = 0;
                    while (depth < limit && alpha(depth) < threshold) depth++;
                    return depth;
                }
                void AssertContourClose(int[] depths) => Assert.True(depths.Max() - depths.Min() <= 1,
                    $"Rounded contours differ at {width}x{height}, alpha {threshold}: {string.Join(",", depths)}.");
            }
            var top = Alpha(renderer.Width / 2, 0);
            var bottom = Alpha(renderer.Width / 2, renderer.Height - 1);
            Assert.InRange(top, 230, 255);
            Assert.InRange(bottom, 230, 255);
            AssertAlphaClose(top, bottom);

            byte Alpha(int x, int y) => renderer.Pixels[(y * renderer.Width + x) * 4 + 3];
            void AssertAlphaClose(byte first, byte second) => Assert.True(Math.Abs(first - second) <= 2,
                $"Rounded silhouette differs at {width}x{height}: alpha {first} versus {second}.");
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
            var movedClip = Path.Combine(folder, "moved.mp4");
            File.Copy(clip, movedClip);
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

            // A freely placed card has its own raster size and explicit output
            // coordinates. Neither its old corner nor the base video controls it.
            var movedSpec = spec with { Transform = new SpotifyOverlayTransform(.125, .5, .5) };
            using var movedAnimation = Pump(SpotifyOverlayAnimation.PrepareAsync(movedSpec, CancellationToken.None));
            Assert.Equal(new SpotifyOverlayBounds(80, 180, 320, 111), movedAnimation.Bounds);
            var movedRaw = ReadPixels(movedAnimation.Path, "-f", "rawvideo", "-pix_fmt", "bgra");
            Assert.Equal(320 * 111 * 4 * 30, movedRaw.Length);
            Assert.Equal(SpotifyOverlayOutcome.Completed, SpotifyOverlayBurner.BurnAsync(movedClip, movedAnimation).GetAwaiter().GetResult());
            var movedPixels = ReadPixels(movedClip, "-f", "rawvideo", "-pix_fmt", "rgb24");
            var movedArtwork = (220 * 640 + 324) * 3;
            Assert.True(Math.Abs(movedPixels[movedArtwork] - movedPixels[movedArtwork + 1]) < 12);
            Assert.True(movedPixels[firstOffset + 1] > movedPixels[firstOffset] + 60);
            var movedUnavailable = 29 * 640 * 360 * 3 + movedArtwork;
            Assert.True(movedPixels[movedUnavailable + 1] > movedPixels[movedUnavailable] + 60);

            // Exercise the export filter order: crop, speed and output scaling
            // happen before the independently sized overlay is composited.
            var scaledSpec = movedSpec with { Width = 320, Height = 180, Duration = .5, Speed = 2 };
            using var scaledAnimation = Pump(SpotifyOverlayAnimation.PrepareAsync(scaledSpec, CancellationToken.None));
            var graph = ClipRenderFilters.ComposeWithAnimation(
                ClipRenderFilters.BuildVideoFilter(new(160, 20, 320, 320), 2, "scale=320:180"),
                spec.Position, "[0:v:0]", "[video]", scaledAnimation.Bounds);
            var scaledClip = Path.Combine(folder, "scaled.mp4");
            Pump(SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "color=green:s=640x360:r=30:d=1",
                "-i", scaledAnimation.Path, "-filter_complex", graph, "-map", "[video]", "-an", "-t", "0.5",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", scaledClip).ContinueWith(task => { task.GetAwaiter().GetResult(); return true; }));
            var scaledPixels = ReadPixels(scaledClip, "-f", "rawvideo", "-pix_fmt", "rgb24");
            var scaledArtwork = (110 * 320 + 162) * 3;
            Assert.True(Math.Abs(scaledPixels[scaledArtwork] - scaledPixels[scaledArtwork + 1]) < 12);
            var scaledOldCorner = (12 * 320 + 306) * 3;
            Assert.True(scaledPixels[scaledOldCorner + 1] > scaledPixels[scaledOldCorner] + 60);
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
