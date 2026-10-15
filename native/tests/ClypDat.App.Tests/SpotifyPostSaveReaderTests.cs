using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// The post-save step must release every reader of the new clip before its own
// work starts. The stand-in job opens the clip exclusively, which fails while
// any reader still holds it.
public sealed class SpotifyPostSaveReaderTests
{
    [Fact]
    public async Task CoordinatorReleasesRealFileHandleBeforeTheJob()
    {
        await WithClip(async clip =>
        {
            using var reader = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await new SpotifyPostSaveCoordinator().RunAsync(clip, "editor", false,
                () => { reader.Dispose(); return Task.CompletedTask; },
                _ => OpenExclusively(clip));
            Assert.Equal(SpotifyOverlayOutcome.Completed, result);
        });
    }

    [Fact]
    public async Task EditorMediaUnloadsAndReloadsWithoutUnloadingAnotherClip()
    {
        await WithClip(async clip =>
        {
            using var playback = new PlaybackSession();
            await playback.LoadVideoAsync(clip);
            Assert.Equal(clip, playback.LoadedPath);
            await playback.UnloadMediaAsync(clip + ".other");
            Assert.Equal(clip, playback.LoadedPath);
            var result = await new SpotifyPostSaveCoordinator().RunAsync(clip, null, false,
                () => playback.UnloadMediaAsync(clip),
                _ => OpenExclusively(clip));
            Assert.Null(playback.LoadedPath);
            Assert.Null(playback.VideoPlayer.Media);
            Assert.Equal(SpotifyOverlayOutcome.Completed, result);
            await playback.LoadVideoAsync(clip);
            Assert.Equal(clip, playback.LoadedPath);
            await playback.UnloadMediaAsync(clip);
        });
    }

    private static Task<SpotifyOverlayOutcome> OpenExclusively(string clip)
    {
        using var exclusive = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return Task.FromResult(SpotifyOverlayOutcome.Completed);
    }

    private static async Task WithClip(Func<string, Task> test)
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat overlay ' 音 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "clip.mp4");
            await TestFfmpeg.Run("-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=1", "-c:v", "libx264", clip);
            await test(clip);
        }
        finally { Directory.Delete(folder, true); }
    }
}
