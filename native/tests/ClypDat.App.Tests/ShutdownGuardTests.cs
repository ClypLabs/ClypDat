using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// ShutdownGuard is process-wide and other tests run guarded code in parallel,
// so assertions only look at the labels each test registers itself.
public sealed class ShutdownGuardTests
{
    [Fact]
    public void LabelsTrackEveryOperationUntilItEnds()
    {
        var first = ShutdownGuard.Begin("Test exporting clip");
        var second = ShutdownGuard.Begin("Test exporting clip");
        try
        {
            Assert.True(ShutdownGuard.IsBusy);
            Assert.Contains("Test exporting clip (2)", ShutdownGuard.ActiveLabels);
            var idle = ShutdownGuard.WaitIdleAsync();
            first.Dispose();
            Assert.False(idle.IsCompleted);
            Assert.Contains("Test exporting clip", ShutdownGuard.ActiveLabels);
            second.Dispose();
            Assert.DoesNotContain(ShutdownGuard.ActiveLabels, label => label.StartsWith("Test exporting clip", StringComparison.Ordinal));
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }
}
