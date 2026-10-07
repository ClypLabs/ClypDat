using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FilmstripFallbackTests
{
    // A tester's old recordings lost their whole filmstrip because one seek
    // point decoded nothing (damaged data). A container claiming more
    // duration than its video holds - a truncated recording - empties the
    // late seek points the same way, deterministically.
    [Fact]
    public async Task SeekPointsWithNoFrameStillProduceAStrip()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        Assert.True(FfmpegPathResolver.IsAvailable);
        var folder = Path.Combine(Path.GetTempPath(), "ClypDat-FilmstripFallbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var media = new MediaProbeService();
        var clip = Path.Combine(folder, "clip.mp4");
        try
        {
            await SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", clip);
            await media.ProbeMetadataAsync(clip);

            var strip = await media.EnsureFilmstripAsync(clip, TimeSpan.FromSeconds(4), frameCount: 4);

            Assert.False(string.IsNullOrEmpty(strip));
            Assert.True(new FileInfo(strip).Length > 0);
        }
        finally
        {
            media.DeleteCacheFor(clip);
            Directory.Delete(folder, true);
        }
    }
}
