using System.Runtime.ExceptionServices;
using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayCardRendererTests
{
    // The card is composited by ffmpeg from a PNG this writes, so "it rendered
    // at all, at a sane size" is the thing worth holding: a zero-byte or
    // zero-sized card fails the whole export filtergraph rather than just
    // looking wrong.
    [Fact]
    public void RendersACardScaledToTheFrame()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunOnUiThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"spotify-card-{Guid.NewGuid():N}.png");
            try
            {
                var card = new SpotifyCard("BOUNCY (K-HOT CHILLI PEPPERS)", "ATEEZ",
                    TimeSpan.FromSeconds(198), TimeSpan.FromSeconds(64), null);

                Assert.Equal(path, SpotifyOverlayCardRenderer.Render(card, 1080, path));
                Assert.True(new FileInfo(path).Length > 0);

                using var rendered = new Avalonia.Media.Imaging.Bitmap(path);
                // 10.4% of the frame, so a 1080p clip gets a 112px card.
                Assert.InRange(rendered.PixelSize.Height, 108, 116);
                // Art, then two lines of text: wider than it is tall, always.
                Assert.True(rendered.PixelSize.Width > rendered.PixelSize.Height * 2);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        });
    }

    // A track with no artist and no position still has to produce a card - that
    // is what a local file or a podcast looks like through the API.
    [Fact]
    public void RendersWithoutArtistOrProgress()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunOnUiThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"spotify-card-{Guid.NewGuid():N}.png");
            try
            {
                var card = new SpotifyCard("Untitled recording", null, null, null, null);
                Assert.NotNull(SpotifyOverlayCardRenderer.Render(card, 1440, path));
                Assert.True(new FileInfo(path).Length > 0);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        });
    }

    [Fact]
    public void RefusesATrackWithNoName()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunOnUiThread(() =>
            Assert.Null(SpotifyOverlayCardRenderer.Render(new SpotifyCard(" ", "Artist", null, null, null), 1080,
                Path.Combine(Path.GetTempPath(), "unused.png"))));
    }

    // Avalonia's rendering needs a platform and an STA thread, the same way
    // ClipOverlayCardRendererTests stands one up.
    private static void RunOnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
                body();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
