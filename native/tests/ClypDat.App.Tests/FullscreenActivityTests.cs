using Avalonia;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullscreenActivityTests
{
    private static bool Poll(FullscreenActivity activity, double seconds, int x = 50,
        bool enabled = true, bool overSurface = true, bool overControls = false, bool captured = false) =>
        activity.Update(TimeSpan.FromSeconds(seconds), new PixelPoint(x, 50), enabled, overSurface, overControls, captured);

    [Fact]
    public void StationaryPointerHidesAtOneSecondEvenWithoutPlayback()
    {
        var activity = new FullscreenActivity();
        Assert.True(Poll(activity, 0));
        Assert.True(Poll(activity, 0.999));
        Assert.False(Poll(activity, 1));
        Assert.False(Poll(activity, 20));
    }

    [Fact]
    public void MovementAndPlaybackInputRestartIdleTimeout()
    {
        var activity = new FullscreenActivity();
        Poll(activity, 0);
        Assert.False(Poll(activity, 1));
        Assert.True(Poll(activity, 2, x: 51));
        Assert.False(Poll(activity, 3, x: 51));
        activity.Record(TimeSpan.FromSeconds(4));
        Assert.True(Poll(activity, 4.999, x: 51));
        Assert.False(Poll(activity, 5, x: 51));
    }

    [Fact]
    public void MovementOutsideScopedSurfacesDoesNotRevealControls()
    {
        var activity = new FullscreenActivity();
        Poll(activity, 0);
        Assert.False(Poll(activity, 1, x: 100, overSurface: false));
        Assert.False(Poll(activity, 2, x: 200, overSurface: false));
        Assert.True(Poll(activity, 3, x: 201));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HoverOrCaptureHoldsControlsUntilOneSecondAfterRelease(bool hover, bool capture)
    {
        var activity = new FullscreenActivity();
        Poll(activity, 0);
        Assert.True(Poll(activity, 20, overControls: hover, captured: capture));
        Assert.True(Poll(activity, 20.999));
        Assert.False(Poll(activity, 21));
    }

    [Fact]
    public void FocusDialogLoadingOrMinimizeGateRestartsVisibilityOnReturn()
    {
        var activity = new FullscreenActivity();
        Poll(activity, 0);
        Assert.False(Poll(activity, 1));
        Assert.False(Poll(activity, 2, enabled: false));
        Assert.True(Poll(activity, 100));
        Assert.False(Poll(activity, 101));
        activity.Suspend();
        Assert.False(activity.ControlsVisible);
        Assert.True(Poll(activity, 102));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeekReleaseAndCaptureLossSettleOnceWithOriginalPlaybackIntent(bool playing)
    {
        var scrub = new SeekRailScrub();
        Assert.True(scrub.Begin(playing));
        Assert.False(scrub.Begin(!playing));
        Assert.True(scrub.Active);
        Assert.Equal(playing, scrub.Finish());
        Assert.False(scrub.Active);
        Assert.Null(scrub.Finish());
        Assert.True(scrub.Begin(!playing));
        Assert.Equal(!playing, scrub.Finish());
    }
}
