using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureLifecycleCoordinatorTests
{
    [Fact]
    public async Task PendingSaveFinishesBeforeSuspendThenLatestWakeRestarts()
    {
        var fixture = new Fixture();
        await fixture.Start();
        await fixture.Saves.WaitAsync();
        fixture.Coordinator.SetAvailability(false);
        var suspend = fixture.Coordinator.ReconcileAsync(default);
        Assert.Equal(new[] { "start" }, fixture.Events);
        fixture.Coordinator.SetAvailability(true);
        var wake = fixture.Coordinator.ReconcileAsync(default);
        fixture.Saves.Release();
        await Task.WhenAll(suspend, wake).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "start", "stop", "start" }, fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeDuringStopUsesLatestUserIntent(bool disable)
    {
        var fixture = new Fixture();
        await fixture.Start();
        fixture.StopBlocked = true;
        fixture.Coordinator.SetAvailability(false);
        var suspend = fixture.Coordinator.ReconcileAsync(default);
        await fixture.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Coordinator.SetAvailability(true);
        if (disable) fixture.Coordinator.Request(false);
        var wake = fixture.Coordinator.ReconcileAsync(default);
        fixture.StopContinue.SetResult();
        await Task.WhenAll(suspend, wake).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(disable ? new[] { "start", "stop" } : new[] { "start", "stop", "start" }, fixture.Events);
        Assert.Equal(!disable, fixture.Recording);
    }

    [Fact]
    public async Task DisableWhileSuspendedPreventsWakeRestart()
    {
        var fixture = new Fixture();
        await fixture.Start();
        fixture.Coordinator.SetAvailability(false);
        await fixture.Coordinator.ReconcileAsync(default);
        fixture.Coordinator.Request(false);
        await fixture.Coordinator.ReconcileAsync(default);
        fixture.Coordinator.SetAvailability(true);
        await fixture.Coordinator.ReconcileAsync(default);
        Assert.Equal(new[] { "start", "stop" }, fixture.Events);
    }

    [Fact]
    public async Task RepeatedNotificationsDoNotRepeatTransitions()
    {
        var fixture = new Fixture();
        await fixture.Start();
        fixture.Coordinator.SetAvailability(true);
        await fixture.Coordinator.ReconcileAsync(default);
        fixture.Coordinator.SetAvailability(false);
        await fixture.Coordinator.ReconcileAsync(default);
        fixture.Coordinator.SetAvailability(false);
        await fixture.Coordinator.ReconcileAsync(default);
        Assert.Equal(new[] { "start", "stop" }, fixture.Events);
    }

    [Fact]
    public async Task SuspendDuringStartStopsBeforeRestart()
    {
        var fixture = new Fixture { StartBlocked = true };
        var start = fixture.Start();
        await fixture.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Coordinator.SetAvailability(false);
        fixture.Coordinator.SetAvailability(true);
        var wake = fixture.Coordinator.ReconcileAsync(default);
        fixture.StartContinue.SetResult();
        await Task.WhenAll(start, wake).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "start", "stop", "start" }, fixture.Events);
    }

    [Fact]
    public async Task DisableDuringPendingSavePreventsRestart()
    {
        var fixture = new Fixture();
        await fixture.Start();
        await fixture.Saves.WaitAsync();
        fixture.Coordinator.SetAvailability(false);
        var suspend = fixture.Coordinator.ReconcileAsync(default);
        fixture.Coordinator.Request(false);
        fixture.Coordinator.SetAvailability(true);
        fixture.Saves.Release();
        await suspend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "start", "stop" }, fixture.Events);
    }

    private sealed class Fixture
    {
        public readonly SemaphoreSlim Saves = new(1, 1);
        public readonly List<string> Events = new();
        public readonly TaskCompletionSource StopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StopContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StartContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Recording, StopBlocked, StartBlocked;
        public readonly CaptureLifecycleCoordinator Coordinator;
        public Fixture()
        {
            Coordinator = new(new SemaphoreSlim(1, 1), Saves, () => Recording,
                async _ =>
                {
                    StartEntered.TrySetResult();
                    if (StartBlocked) await StartContinue.Task;
                    Events.Add("start"); Recording = true;
                },
                async _ =>
                {
                    StopEntered.TrySetResult();
                    if (StopBlocked) await StopContinue.Task;
                    Events.Add("stop"); Recording = false;
                }, (_, _) => { });
        }
        public Task Start()
        {
            Coordinator.Request(true);
            Coordinator.SetAvailability(true);
            return Coordinator.ReconcileAsync(default);
        }
    }
}
