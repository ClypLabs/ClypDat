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

    [Theory]
    [InlineData(0x7)]
    [InlineData(0x12)]
    public void FullSleepWithoutDisplayOffRequiresResume(int resume)
    {
        var policy = new CaptureAvailabilityPolicy();
        Assert.False(policy.HandlePowerEvent(0x4));
        Assert.False(policy.SetDisplayState(1));
        Assert.False(policy.HandleSessionEvent(0x8));
        Assert.True(policy.HandlePowerEvent(resume));
        Assert.True(policy.HandlePowerEvent(resume));
    }

    [Fact]
    public void LockedWakeAndConnectRemainUnavailableUntilUnlock()
    {
        var policy = new CaptureAvailabilityPolicy();
        Assert.False(policy.HandleSessionEvent(0x7));
        Assert.False(policy.HandlePowerEvent(0x4));
        Assert.False(policy.HandlePowerEvent(0x12));
        Assert.False(policy.HandleSessionEvent(0x1));
        Assert.True(policy.HandleSessionEvent(0x8));
    }

    [Theory]
    [InlineData(0x2, 0x1)]
    [InlineData(0x4, 0x3)]
    public void LiteralDisconnectConnectValues(int disconnect, int connect)
    {
        var policy = new CaptureAvailabilityPolicy();
        Assert.False(policy.HandleSessionEvent(disconnect));
        Assert.False(policy.HandleSessionEvent(0x8));
        Assert.True(policy.HandleSessionEvent(connect));
    }

    [Fact]
    public void MonitorFailureBlocksAllResumeNotifications()
    {
        var policy = new CaptureAvailabilityPolicy();
        Assert.False(policy.SetMonitoringReady(false));
        Assert.False(policy.HandlePowerEvent(0x12));
        Assert.False(policy.HandleSessionEvent(0x8));
        Assert.False(policy.SetDisplayState(1));
        Assert.True(policy.SetMonitoringReady(true));
    }

    [Fact]
    public void UnknownSessionEventDoesNotCopyDisplayStateIntoSessionState()
    {
        var policy = new CaptureAvailabilityPolicy();
        policy.SetDisplayState(0);
        Assert.False(policy.HandleSessionEvent(0x99));
        Assert.True(policy.SetDisplayState(1));
    }
}
