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

    [Fact]
    public void KeyedOperationsIgnoreDuplicateStartsAndEnds()
    {
        const string key = "worker-save:test";
        try
        {
            ShutdownGuard.BeginKeyed(key, "Test saving clip");
            ShutdownGuard.BeginKeyed(key, "Test saving clip");
            Assert.Contains("Test saving clip", ShutdownGuard.ActiveLabels);
            Assert.DoesNotContain("Test saving clip (2)", ShutdownGuard.ActiveLabels);
            ShutdownGuard.EndKeyed(key);
            ShutdownGuard.EndKeyed(key);
            Assert.DoesNotContain("Test saving clip", ShutdownGuard.ActiveLabels);
        }
        finally
        {
            ShutdownGuard.EndKeyed(key);
        }
    }

    [Fact]
    public void DisposingATokenTwiceEndsItOnce()
    {
        var kept = ShutdownGuard.Begin("Test moving clip");
        var disposed = ShutdownGuard.Begin("Test deleting clip");
        try
        {
            disposed.Dispose();
            disposed.Dispose();
            Assert.Contains("Test moving clip", ShutdownGuard.ActiveLabels);
            Assert.DoesNotContain("Test deleting clip", ShutdownGuard.ActiveLabels);
        }
        finally
        {
            kept.Dispose();
        }
        Assert.DoesNotContain("Test moving clip", ShutdownGuard.ActiveLabels);
    }
}
