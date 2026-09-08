using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureAvailabilityPolicyTests
{
    [Fact]
    public void DisplayOff_PausesUntilDisplayReturns()
    {
        var policy = new CaptureAvailabilityPolicy();

        Assert.False(policy.SetDisplayState(0));
        Assert.False(policy.IsAvailable);
        Assert.True(policy.SetDisplayState(1));
    }

    [Fact]
    public void SessionLock_BlocksDisplayResumeUntilUnlock()
    {
        var policy = new CaptureAvailabilityPolicy();

        Assert.False(policy.SetSessionAvailable(false));
        Assert.False(policy.SetDisplayState(1));
        Assert.True(policy.SetSessionAvailable(true));
    }

    [Fact]
    public void DisplayDim_RemainsAvailable()
    {
        var policy = new CaptureAvailabilityPolicy();

        Assert.True(policy.SetDisplayState(2));
        Assert.True(policy.IsAvailable);
    }
}
