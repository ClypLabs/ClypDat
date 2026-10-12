using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LaunchPresentationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void LocalPublishRestartsNeverRequestForeground(bool gameDetected, bool detectionFailed, bool minimized)
    {
        var presentation = LaunchPresentationPolicy.Resolve(["--publish-restart"], gameDetected, detectionFailed);
        Assert.Equal(minimized ? LaunchPresentation.Minimized : LaunchPresentation.Restart, presentation);
        Assert.False(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.False(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }

    [Fact]
    public void UserLaunchesStillGetTheInteractiveLoader()
    {
        var presentation = LaunchPresentationPolicy.Resolve([]);
        Assert.Equal(LaunchPresentation.Normal, presentation);
        Assert.True(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.True(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }
}
