using System.Diagnostics;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayBurnerTests
{
    [Fact]
    public async Task ReleasedPlaybackHandleAllowsRealBurnReplacement()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        Assert.True(FfmpegPathResolver.IsAvailable);
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat overlay ' 音 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "clip.mp4");
            var card = Path.Combine(folder, "card.png");
            await Run("-f", "lavfi", "-i", "color=black:s=320x180:r=25:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "0:v", "-map", "1:a", "-map", "1:a", "-map", "1:a", "-map", "1:a", "-t", "1", "-c:v", "libx264", "-c:a", "aac", "-metadata", "comment=CLYPDAT_CAPTURE_BACKEND=test", "-metadata", "title=It's 音", "-movflags", "use_metadata_tags", clip);
            await Run("-f", "lavfi", "-i", "color=red:s=80x40", "-frames:v", "1", card);
            var original = await SpotifyOverlayBurner.InspectAsync(clip);
            var created = File.GetCreationTimeUtc(clip);
            var media = new MediaProbeService();
            await media.ProbeMetadataAsync(clip);
            var thumbnail = await media.EnsureThumbnailAsync(clip, TimeSpan.FromSeconds(1));
            var filmstrip = await media.EnsureFilmstripAsync(clip, TimeSpan.FromSeconds(1));
            var thumbnailBefore = await File.ReadAllBytesAsync(thumbnail);
            var filmstripBefore = await File.ReadAllBytesAsync(filmstrip);
            using var reader = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(1500); reader.Dispose(); });
            var result = await SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left");
            await release;
            Assert.Equal(SpotifyOverlayOutcome.Completed, result);
            var installed = await SpotifyOverlayBurner.InspectAsync(clip);
            Assert.True(SpotifyOverlayBurner.IsValidReplacement(original, installed));
            Assert.Equal(4, installed.Audio.Length);
            Assert.Equal(created, File.GetCreationTimeUtc(clip));
            Assert.Equal("It's 音", installed.Tags["title"]);
            Assert.Equal(SpotifyOverlayOutcome.Skipped, await SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left"));
            media.DeleteCacheFor(clip);
            Assert.False(File.Exists(thumbnail));
            Assert.False(File.Exists(filmstrip));
            Assert.True((await media.ProbeMetadataAsync(clip)).SpotifyOverlayBurned);
            var thumbnailAfter = await File.ReadAllBytesAsync(await media.EnsureThumbnailAsync(clip, TimeSpan.FromSeconds(1)));
            var filmstripAfter = await File.ReadAllBytesAsync(await media.EnsureFilmstripAsync(clip, TimeSpan.FromSeconds(1)));
            Assert.False(thumbnailBefore.SequenceEqual(thumbnailAfter));
            Assert.False(filmstripBefore.SequenceEqual(filmstripAfter));
            var pixels = Path.Combine(folder, "pixel.rgb");
            await Run("-i", clip, "-vf", "crop=2:2:20:20", "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", pixels);
            var rgb = await File.ReadAllBytesAsync(pixels);
            Assert.True(rgb[0] > 200 && rgb[1] < 40 && rgb[2] < 40, "Overlay must contain red pixels.");
            media.DeleteCacheFor(clip);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PersistentPlaybackLockPreservesOriginalAndExplicitRetrySucceeds()
    {
        await WithClip(async (clip, card) =>
        {
            var original = await File.ReadAllBytesAsync(clip);
            var coordinator = new SpotifyPostSaveCoordinator();
            using (var reader = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await coordinator.RunAsync(clip, "save", false, () => Task.CompletedTask,
                    token => SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", token));
                Assert.Equal(SpotifyOverlayOutcome.Failed, result);
                Assert.True(SpotifyProcessingPaths.Failed(clip));
                Assert.Equal(original, await File.ReadAllBytesAsync(clip));
            }
            var retry = await coordinator.RunAsync(clip, "save", true, () => Task.CompletedTask,
                token => SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", token));
            Assert.Equal(SpotifyOverlayOutcome.Completed, retry);
            Assert.False(SpotifyProcessingPaths.Failed(clip));
        });
    }

    [Fact]
    public async Task CoordinatorReleasesRealPlaybackHandleBeforeBurn()
    {
        await WithClip(async (clip, card) =>
        {
            using var reader = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await new SpotifyPostSaveCoordinator().RunAsync(clip, "editor", false,
                () => { reader.Dispose(); return Task.CompletedTask; },
                token => SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", token));
            Assert.Equal(SpotifyOverlayOutcome.Completed, result);
        });
    }

    [Fact]
    public async Task EditorMediaUnloadsAndReloadsWithoutUnloadingAnotherClip()
    {
        await WithClip(async (clip, card) =>
        {
            using var playback = new PlaybackSession();
            await playback.LoadVideoAsync(clip);
            Assert.Equal(clip, playback.LoadedPath);
            await playback.UnloadMediaAsync(clip + ".other");
            Assert.Equal(clip, playback.LoadedPath);
            var result = await new SpotifyPostSaveCoordinator().RunAsync(clip, null, false,
                () => playback.UnloadMediaAsync(clip),
                token => SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", token));
            Assert.Null(playback.LoadedPath);
            Assert.Null(playback.VideoPlayer.Media);
            Assert.Equal(SpotifyOverlayOutcome.Completed, result);
            await playback.LoadVideoAsync(clip);
            Assert.Equal(clip, playback.LoadedPath);
            await playback.UnloadMediaAsync(clip);
        });
    }

    [Fact]
    public async Task EncoderFailureFallsBackToSoftware()
    {
        await WithClip(async (clip, card) => Assert.Equal(SpotifyOverlayOutcome.Completed,
            await SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", preferredCodec: ["-c:v", "unavailable_encoder"])));
    }

    [Fact]
    public async Task CancellationStopsEncoderAndCleansWorkFiles()
    {
        await WithClip(async (clip, card) =>
        {
            var original = await File.ReadAllBytesAsync(clip);
            using var cancellation = new CancellationTokenSource();
            var burn = SpotifyOverlayBurner.BurnAsync(clip, card, "Top Left", cancellation.Token,
                ["-c:v", "libx264", "-preset", "veryslow"]);
            var folder = Path.GetDirectoryName(clip)!;
            while (!Directory.EnumerateDirectories(folder, ".clypdat-overlay-*").Any() && !burn.IsCompleted)
                await Task.Delay(5);
            await Task.Delay(100);
            cancellation.Cancel();
            Assert.Equal(SpotifyOverlayOutcome.Cancelled, await burn);
            Assert.Equal(original, await File.ReadAllBytesAsync(clip));
            Assert.Empty(Directory.EnumerateDirectories(folder, ".clypdat-overlay-*"));
            using var exclusive = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }, duration: "20");
    }

    private static async Task WithClip(Func<string, string, Task> test, string duration = "1")
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat overlay ' 音 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "clip.mp4");
            var card = Path.Combine(folder, "card.png");
            await Run("-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=" + duration, "-c:v", "libx264", clip);
            await Run("-f", "lavfi", "-i", "color=red:s=80x40", "-frames:v", "1", card);
            await test(clip, card);
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static async Task Run(params string[] arguments)
    {
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-v"); process.StartInfo.ArgumentList.Add("error");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
