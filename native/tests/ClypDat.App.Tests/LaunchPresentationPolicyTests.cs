using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LaunchPresentationPolicyTests
{
    [Fact]
    public void NoArguments_IsInteractiveNormalLaunch()
    {
        var presentation = LaunchPresentationPolicy.Resolve([]);

        Assert.Equal(LaunchPresentation.Normal, presentation);
        Assert.True(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.True(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }

    [Fact]
    public void Restart_IsPassiveVisibleLaunch()
    {
        var presentation = LaunchPresentationPolicy.Resolve(["--restart"]);

        Assert.Equal(LaunchPresentation.Restart, presentation);
        Assert.False(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.False(LaunchPresentationPolicy.StartsInTray(presentation));
        Assert.False(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }

    [Fact]
    public void Minimized_IsPassiveTrayLaunch()
    {
        var presentation = LaunchPresentationPolicy.Resolve(["--minimized"]);

        Assert.Equal(LaunchPresentation.Minimized, presentation);
        Assert.False(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.True(LaunchPresentationPolicy.StartsInTray(presentation));
        Assert.False(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }

    [Fact]
    public void Minimized_WinsWhenArgumentsConflict()
    {
        Assert.Equal(LaunchPresentation.Minimized,
            LaunchPresentationPolicy.Resolve(["--restart", "--minimized"]));
    }
}
