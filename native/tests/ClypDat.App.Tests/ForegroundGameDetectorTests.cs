using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ForegroundGameDetectorTests
{
    [Theory]
    [InlineData(true, 4321u, 4321, true)]
    [InlineData(false, 4321u, 4321, false)]
    [InlineData(true, 9999u, 4321, false)]
    [InlineData(true, 0u, 4321, false)]
    public void HeldGameSurvivesMinimiseButNotWindowDeathOrHandleReuse(
        bool windowExists, uint windowProcessId, int detectionProcessId, bool expected)
    {
        Assert.Equal(expected, ForegroundGameDetector.IsHeldGameStillRunning(
            windowExists, windowProcessId, detectionProcessId));
    }
}
