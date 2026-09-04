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

    [Fact]
    public void PublishRestart_WithoutForegroundGame_IsInteractiveNormalLaunch()
    {
        var arguments = new[] { "--publish-restart" };

        Assert.True(LaunchPresentationPolicy.RequiresForegroundGameCheck(arguments));
        var presentation = LaunchPresentationPolicy.Resolve(arguments, foregroundGameDetected: false);

        Assert.Equal(LaunchPresentation.Normal, presentation);
        Assert.True(LaunchPresentationPolicy.UsesStartupLoader(presentation));
        Assert.True(LaunchPresentationPolicy.ActivatesAfterStartupLoader(presentation));
    }

    [Fact]
    public void PublishRestart_WithForegroundGame_StartsInTray()
    {
        var presentation = LaunchPresentationPolicy.Resolve(["--publish-restart"], foregroundGameDetected: true);

        Assert.Equal(LaunchPresentation.Minimized, presentation);
        Assert.True(LaunchPresentationPolicy.StartsInTray(presentation));
        Assert.False(LaunchPresentationPolicy.UsesStartupLoader(presentation));
    }

    [Fact]
    public void PublishRestart_WhenGameDetectionFails_StartsInTray()
    {
        var presentation = LaunchPresentationPolicy.Resolve(["--publish-restart"], foregroundGameDetectionFailed: true);

        Assert.Equal(LaunchPresentation.Minimized, presentation);
    }

    [Fact]
    public void ExplicitMinimized_WinsOverPublishRestart()
    {
        var arguments = new[] { "--publish-restart", "--minimized" };

        Assert.False(LaunchPresentationPolicy.RequiresForegroundGameCheck(arguments));
        Assert.Equal(LaunchPresentation.Minimized, LaunchPresentationPolicy.Resolve(arguments));
    }
}
