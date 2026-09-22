using System.Diagnostics;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayBurnerTests
{
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
