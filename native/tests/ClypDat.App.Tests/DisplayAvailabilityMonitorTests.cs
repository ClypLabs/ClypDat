using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DisplayAvailabilityMonitorTests
{
    [Fact]
    public void FailedSuspendRegistrationNeverPublishesAvailable()
    {
        var attempted = false;
        var published = new List<bool>();
        using var monitor = new DisplayAvailabilityMonitor((_, _) =>
        {
            attempted = true;
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(5);
            return nint.Zero;
        });
        monitor.AvailabilityChanged += (_, available) => published.Add(available);
        monitor.Start();
        monitor.Dispose();
        Assert.True(attempted);
        Assert.False(monitor.IsAvailable);
        Assert.DoesNotContain(true, published);
    }
}
