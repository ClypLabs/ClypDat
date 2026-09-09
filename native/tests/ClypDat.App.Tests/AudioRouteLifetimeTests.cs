using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AudioRouteLifetimeTests
{
    [Fact]
    public async Task StalledDiscoveryCannotCreateCaptureOrTimerAfterStopAndRestart()
    {
        var lifetime = new AudioRouteLifetime();
        var generation = lifetime.Start(() => { });
        var discovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = Task.Run(async () =>
        {
            await discovery.Task;
            return lifetime.TryApply(generation, () => throw new Xunit.Sdk.XunitException("Stale capture/timer created"));
        });
        lifetime.Stop(() => { });
        lifetime.Start(() => { });
        discovery.SetResult();
        Assert.False(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(lifetime.TryApply(lifetime.Generation, () => { }));
    }

    [Fact]
    public void RepeatedStopInvalidatesQueuedTimer()
    {
        var lifetime = new AudioRouteLifetime();
        var generation = lifetime.Start(() => { });
        lifetime.Stop(() => { });
        lifetime.Stop(() => { });
        Assert.False(lifetime.TryApply(generation, () => throw new Exception()));
        Assert.False(lifetime.TryApply(lifetime.Generation, () => throw new Exception()));
    }
}
