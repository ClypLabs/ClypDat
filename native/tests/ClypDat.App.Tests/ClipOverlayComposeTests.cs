using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayComposeTests
{
    private static ClipRenderFilters.OverlayComposite Layer(string label, string? enable = null, bool straight = false) =>
        new(new SpotifyOverlayBounds(10, 20, 320, 180), enable, straight, label);

    [Fact]
    public void EachLayerScalesToItsOwnBoxAndChainsIntoTheNext()
    {
        var graph = ClipRenderFilters.ComposeWithOverlays("setpts=PTS/2",
            [Layer("[1:v:0]"), Layer("[2:v:0]", straight: true)], "[0:v:0]", "[vout]");

        Assert.Equal(
            "[0:v:0]setpts=PTS/2[ovbase]" +
            ";[1:v:0]scale=320:180:flags=lanczos[ovl0]" +
            ";[ovbase][ovl0]overlay=10:20:eof_action=pass:repeatlast=0[ovs0]" +
            ";[2:v:0]scale=320:180:flags=lanczos[ovl1]" +
            ";[ovs0][ovl1]overlay=10:20:eof_action=pass:repeatlast=0:alpha=straight[vout]",
            graph);
    }

    [Fact]
    public void TheSpotifyGraphIsUnchangedSoLegacyBurnsStillWork()
    {
        // SpotifyOverlayBurner drives corner placement with symbolic
        // expressions and no scale at all; that branch must stay untouched.
        var graph = ClipRenderFilters.ComposeWithAnimation(null, "Top Right", "[0:v:0]", "[vout]");

        Assert.Contains("overlay=main_w-overlay_w:", graph);
        Assert.DoesNotContain("scale=", graph);
    }
}
