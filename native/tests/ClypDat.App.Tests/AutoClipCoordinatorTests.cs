using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AutoClipCoordinatorTests
{
    [Fact]
    public async Task RejectsWrongSessionGameDisabledEventAndDuplicateOccurrence()
    {
        var session = Guid.NewGuid();
        await using var coordinator = new AutoClipCoordinator((_, _) => Task.CompletedTask);
        await coordinator.ReconcileAsync(new AutoClipPolicy(session, "fortnite", true,
            new HashSet<string> { "victory-royale" }, TimeSpan.FromMinutes(2)));

        Assert.False(await coordinator.ObserveAsync(Signal(Guid.NewGuid(), "fortnite", "victory-royale", "one")));
        Assert.False(await coordinator.ObserveAsync(Signal(session, "helldivers2", "victory-royale", "two")));
        Assert.False(await coordinator.ObserveAsync(Signal(session, "fortnite", "headshot", "three")));
        Assert.True(await coordinator.ObserveAsync(Signal(session, "fortnite", "victory-royale", "four")));
        Assert.False(await coordinator.ObserveAsync(Signal(session, "fortnite", "victory-royale", "four")));
    }

    [Fact]
    public async Task MergesOverlapsAndUsesDominantEvent()
    {
        var saved = new TaskCompletionSource<AutoClipPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Guid.NewGuid();
        await using var coordinator = new AutoClipCoordinator((plan, _) => { saved.TrySetResult(plan); return Task.CompletedTask; });
        await coordinator.ReconcileAsync(new AutoClipPolicy(session, "fortnite", true,
            new HashSet<string> { "distance-shot", "impossible-shot" }, TimeSpan.FromMinutes(2)));
        var now = DateTime.UtcNow;

        Assert.True(await coordinator.ObserveAsync(Signal(session, "fortnite", "distance-shot", "distance", now, 20, 0.15)));
        Assert.True(await coordinator.ObserveAsync(Signal(session, "fortnite", "impossible-shot", "impossible", now.AddMilliseconds(20), 30, 0.15)));

        var plan = await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("impossible-shot", plan.DominantEventId);
        Assert.Equal(new[] { "distance-shot", "impossible-shot" }, plan.EventIds.OrderBy(item => item));
        Assert.Equal(2, plan.OccurrenceIds.Count);
    }

    [Fact]
    public async Task SessionSwitchCancelsQueuedTailAndClearsOccurrences()
    {
        var saves = 0;
        var first = Guid.NewGuid();
        await using var coordinator = new AutoClipCoordinator((_, _) => { Interlocked.Increment(ref saves); return Task.CompletedTask; });
        var enabled = new HashSet<string> { "victory-royale" };
        await coordinator.ReconcileAsync(new AutoClipPolicy(first, "fortnite", true, enabled, TimeSpan.FromMinutes(2)));
        Assert.True(await coordinator.ObserveAsync(Signal(first, "fortnite", "victory-royale", "win", tailSeconds: 1)));

        var second = Guid.NewGuid();
        await coordinator.ReconcileAsync(new AutoClipPolicy(second, "fortnite", true, enabled, TimeSpan.FromMinutes(2)));
        await Task.Delay(1100);
        Assert.Equal(0, Volatile.Read(ref saves));
        Assert.True(await coordinator.ObserveAsync(Signal(second, "fortnite", "victory-royale", "win")));
    }

    [Fact]
    public async Task QueueIsBoundedToThirtyTwoPlans()
    {
        var session = Guid.NewGuid();
        var now = DateTime.UtcNow.AddHours(1);
        await using var coordinator = new AutoClipCoordinator((_, _) => Task.CompletedTask, () => DateTime.UtcNow);
        await coordinator.ReconcileAsync(new AutoClipPolicy(session, "fortnite", true,
            new HashSet<string> { "victory-royale" }, TimeSpan.FromHours(2)));

        for (var index = 0; index < 40; index++)
            await coordinator.ObserveAsync(Signal(session, "fortnite", "victory-royale", index.ToString(), now.AddMinutes(index * 3), tailSeconds: 1));

        Assert.Equal(AutoClipCoordinator.Capacity, coordinator.PendingCount);
    }

    private static AutoClipSignal Signal(Guid session, string game, string eventId, string occurrence,
        DateTime? timestamp = null, int priority = 100, double tailSeconds = 10) =>
        new(session, "test-provider", game, eventId, eventId, occurrence, 0.99,
            timestamp ?? DateTime.UtcNow, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(tailSeconds), priority);
}
