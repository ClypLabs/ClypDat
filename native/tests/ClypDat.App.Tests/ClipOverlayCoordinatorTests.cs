using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayCoordinatorTests
{
    private readonly DateTime _epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CompletionDuringEntry_ReplacesImmediately_AndOldTimerCannotResurrect()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var sounds = 0;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => sounds++, () => _epoch, play => play());
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch));
        Assert.Contains(TimeSpan.FromSeconds(3), scheduler.Delays);
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 80, _epoch));
        Assert.Equal(2, scheduler.Delays.Count(delay => delay == TimeSpan.FromSeconds(3)));

        Assert.Equal(2, surface.Presentations.Count);
        Assert.Equal("Clip Saved", surface.Presentations[^1].Event.Title);
        scheduler.Fire(0);
        Assert.Empty(surface.Dismissals);
        scheduler.Fire(3);
        Assert.Single(surface.Dismissals);
        Assert.Equal(1, sounds);
    }

    [Fact]
    public void CompletionUsesFreshPrimaryTarget()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch);
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch));
        var currentPrimary = new ClipOverlayTarget("DISPLAY3", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 1.5, ClipOverlayTargetReason.Primary);
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 80, _epoch) with { Target = currentPrimary });

        Assert.Equal(currentPrimary, surface.Presentations[^1].Event.Target);
    }

    // Equal priority: the later arrival takes the surface, whatever its
    // request stamp says. Lower priority waits instead of vanishing.
    [Fact]
    public void EqualPriorityFollowsArrivalAndLowerPriorityWaits()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch));
        var hint = Event(Guid.NewGuid(), 0, ClipOverlayKind.Standalone, 30, _epoch.AddSeconds(1));
        coordinator.Publish(hint);
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch.AddSeconds(-1)));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch.AddSeconds(2)));
        Assert.Equal(3, surface.Presentations.Count);
        Assert.Equal(1, coordinator.PendingCount);
        scheduler.FireLatestDwell();
        Assert.Equal(hint.WorkflowId, surface.Presentations[^1].Event.WorkflowId);
        Assert.Empty(surface.Dismissals); // Handed straight over, no exit first.
    }

    // The loss seen in the field: a second hotkey press is refused ("A replay
    // save is already in progress"), its Failure takes the surface, and the
    // real save's Saved used to be dropped because Failure outranks it.
    [Fact]
    public void SavedBehindAnotherSavesFailureIsShownAfterIt()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        var real = Guid.NewGuid(); var refused = Guid.NewGuid();
        coordinator.Publish(Event(real, 0, ClipOverlayKind.Saving, 80, _epoch));
        coordinator.Publish(Event(refused, 0, ClipOverlayKind.Saving, 80, _epoch.AddSeconds(1)));
        coordinator.Publish(Event(refused, 1, ClipOverlayKind.Failure, 100, _epoch.AddSeconds(1)));
        coordinator.Publish(Event(real, 1, ClipOverlayKind.Saved, 80, _epoch));
        Assert.Equal(ClipOverlayKind.Failure, surface.Presentations[^1].Event.Kind);
        Assert.Equal(1, coordinator.PendingCount);
        scheduler.FireLatestDwell();
        Assert.Equal(ClipOverlayKind.Saved, surface.Presentations[^1].Event.Kind);
        Assert.Equal(real, surface.Presentations[^1].Event.WorkflowId);
        Assert.Equal(4, counters.Presented);
        Assert.Equal(0, counters.Skipped);
    }

    // A Saving still waiting when its Saved arrives becomes the Saved: the
    // stale Saving is never shown first.
    [Fact]
    public void WaitingSavingIsReplacedByItsSaved()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Failure, 100, _epoch));
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch));
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 80, _epoch));
        Assert.Equal(1, coordinator.PendingCount);
        scheduler.FireLatestDwell();
        Assert.Equal(2, surface.Presentations.Count);
        Assert.Equal(ClipOverlayKind.Saved, surface.Presentations[^1].Event.Kind);
        Assert.DoesNotContain(surface.Presentations, presentation => presentation.Event.Kind == ClipOverlayKind.Saving);
        Assert.Equal(1, counters.Skipped); // The Saving, superseded by its stage.
    }

    // Two saves finishing close together: both results are seen.
    [Fact]
    public void TwoSavesCompletingTogetherBothPresent()
    {
        var surface = new FakeSurface { AutoPresent = false }; var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        coordinator.Publish(Event(first, 1, ClipOverlayKind.Saved, 80, _epoch));
        coordinator.Publish(Event(second, 1, ClipOverlayKind.Saved, 80, _epoch.AddMilliseconds(5)));
        // The first was replaced before it reached the screen: it waits.
        surface.Complete(1, false, "superseded-before-accept");
        surface.Complete(2, true);
        Assert.Equal(1, coordinator.PendingCount);
        scheduler.FireLatestDwell();
        Assert.Equal(first, surface.Presentations[^1].Event.WorkflowId);
        surface.Complete(3, true);
        Assert.Equal(2, counters.Presented);
        Assert.Equal(0, counters.Skipped);
    }

    // A burst cannot build a backlog: one waiting card per kind, at most
    // PendingCapacity, least important dropped first, each with a reason.
    [Fact]
    public void QueueCoalescesAndStaysBounded()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Failure, 100, _epoch));
        for (var i = 0; i < 10; i++) coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.AutoClip, 50, _epoch));
        Assert.Equal(1, coordinator.PendingCount);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 80, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.GameStarted, 40, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Standalone, 30, _epoch));
        Assert.Equal(ClipOverlayCoordinator.PendingCapacity, coordinator.PendingCount);
        Assert.Equal(10, counters.Skipped); // Nine coalesced auto-clips, then the Standalone refused for room.
        scheduler.FireLatestDwell();
        Assert.Equal(ClipOverlayKind.Saved, surface.Presentations[^1].Event.Kind);
        scheduler.FireLatestDwell();
        Assert.Equal(ClipOverlayKind.AutoClip, surface.Presentations[^1].Event.Kind);
        scheduler.FireLatestDwell();
        Assert.Equal(ClipOverlayKind.GameStarted, surface.Presentations[^1].Event.Kind);
    }

    [Fact]
    public void WaitingNotificationsExpire()
    {
        var now = _epoch;
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => now, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Failure, 100, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.GameStarted, 40, _epoch));
        now = _epoch.AddSeconds(6);
        scheduler.FireLatestDwell();
        Assert.Single(surface.Presentations);
        Assert.Single(surface.Dismissals);
        Assert.Equal(1, counters.Skipped);
        Assert.Equal(0, coordinator.PendingCount);
    }

    // A presentation that never completes is failed at the timeout, and what
    // waited behind it goes next.
    [Fact]
    public void PresentationTimeoutFailsAndPromotesWaiting()
    {
        var surface = new FakeSurface { AutoPresent = false }; var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Failure, 100, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 80, _epoch));
        scheduler.Fire(0); // Presentation timeout of the Failure.
        Assert.Equal(1, counters.Failed);
        Assert.Equal(1, counters.TimedOut);
        Assert.Equal([1L], surface.Dismissals);
        Assert.Equal(ClipOverlayKind.Saved, surface.Presentations[^1].Event.Kind);
        surface.Complete(1, true); // Too late: already failed, ignored.
        Assert.Equal(0, counters.Presented);
    }

    [Fact]
    public void FailedPresentationPromotesWaiting()
    {
        var surface = new FakeSurface { AutoPresent = false }; var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, counters: counters);
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Failure, 100, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 80, _epoch));
        surface.Complete(1, false, "window-not-visible-after-recovery");
        Assert.Equal(1, counters.Failed);
        Assert.Equal(ClipOverlayKind.Saved, surface.Presentations[^1].Event.Kind);
        surface.Complete(2, true);
        Assert.Equal(1, counters.Presented);
    }

    // Saving and Saved stay one card on one monitor: a later stage that could
    // only resolve the primary display keeps the game's monitor.
    [Fact]
    public void LaterStageKeepsTheGameMonitorWhenItOnlyFoundTheFallback()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch);
        var game = new ClipOverlayTarget("DISPLAY2", new PixelRect(2560, 0, 1920, 1080), new PixelRect(2560, 0, 1920, 1040), 1, ClipOverlayTargetReason.GameWindow, 42);
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch) with { Target = game });
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 80, _epoch));
        Assert.Equal(game, surface.Presentations[^1].Event.Target);
    }

    // Every notification ends in exactly one outcome.
    [Fact]
    public void EveryNotificationGetsExactlyOneOutcome()
    {
        var random = new Random(7);
        var surface = new FakeSurface { AutoPresent = false }; var scheduler = new FakeScheduler(); var counters = new ClipOverlayCounters();
        var now = _epoch;
        var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => now, counters: counters, log: (_, _) => { });
        var kinds = new[] { (ClipOverlayKind.Saving, 80), (ClipOverlayKind.Saved, 80), (ClipOverlayKind.Failure, 100), (ClipOverlayKind.AutoClip, 50), (ClipOverlayKind.GameStarted, 40) };
        var workflows = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        for (var step = 0; step < 400; step++)
        {
            switch (random.Next(4))
            {
                case 0:
                case 1:
                    var (kind, priority) = kinds[random.Next(kinds.Length)];
                    coordinator.Publish(Event(workflows[random.Next(workflows.Length)], random.Next(3), kind, priority, now) with { ShowVisual = random.Next(10) > 0 });
                    break;
                case 2: surface.CompleteAny(random); break;
                default: now = now.AddMilliseconds(random.Next(3000)); scheduler.FireAny(random); break;
            }
        }
        coordinator.Dispose();
        surface.CompleteAll();
        Assert.Equal(counters.Requested, counters.Presented + counters.Unconfirmed + counters.Skipped + counters.Failed);
    }

    [Fact]
    public void GameHintHasReadingTimeButSaveStillReplacesItImmediately()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var sounds = 0;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => sounds++, () => _epoch, play => play());
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.GameStarted, 40, _epoch));
        Assert.Contains(TimeSpan.FromSeconds(5), scheduler.Delays);
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Saving, 80, _epoch));
        Assert.Equal(2, surface.Presentations.Count);
        Assert.Contains(TimeSpan.FromSeconds(3), scheduler.Delays);
        Assert.Equal(0, sounds);
    }

    [Fact]
    public void DuplicateRegressiveAndRecoveredEventsAreSuppressed_ButLiveSoundIsIndependent()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var sounds = 0;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => sounds++, () => _epoch, play => play());
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 10, _epoch));
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 10, _epoch));
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 1, _epoch) with { IsRecovered = true });
        Assert.Single(surface.Presentations);
        Assert.Equal(1, sounds);
    }

    [Fact]
    public void HundredEventBurstLeavesOnlyLatestVisibleGeneration()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, log: (_, _) => { });
        for (var i = 0; i < 100; i++)
            coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Standalone, 30, _epoch.AddMilliseconds(i)));
        Assert.Equal(100, surface.Presentations.Count);
        scheduler.FireAll();
        Assert.Single(surface.Dismissals);
        Assert.Equal(100, surface.Dismissals[0]);
    }

    [Fact]
    public void DwellStartsOnlyAfterTheSurfaceIsVisible()
    {
        var surface = new FakeSurface { AutoPresent = false };
        var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch);

        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Saving, 80, _epoch));
        Assert.Equal([TimeSpan.FromSeconds(5)], scheduler.Delays);

        surface.CompleteLast(true);
        Assert.Equal(TimeSpan.FromSeconds(3), scheduler.Delays[^1]);
    }

    [Fact]
    public void WorkflowStageHistoryEvictsOldestAfterLimit()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, () => _epoch, log: (_, _) => { });
        var workflows = Enumerable.Range(0, 513).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < workflows.Length; index++)
            coordinator.Publish(Event(workflows[index], 0, ClipOverlayKind.Standalone, 30, _epoch.AddMilliseconds(index)));

        coordinator.Publish(Event(workflows[^1], 0, ClipOverlayKind.Standalone, 30, _epoch.AddSeconds(1)));
        Assert.Equal(513, surface.Presentations.Count);
        coordinator.Publish(Event(workflows[0], 0, ClipOverlayKind.Standalone, 30, _epoch.AddSeconds(2)));
        Assert.Equal(514, surface.Presentations.Count);
    }

    [Theory]
    [InlineData(0, -1920, 64)]
    [InlineData(1, -400, 64)]
    [InlineData(2, -1920, 880)]
    [InlineData(3, -400, 880)]
    [InlineData(4, -1920, 1696)]
    [InlineData(5, -400, 1696)]
    public void PlacementHandlesNegativeCoordinatesAndMixedDpi(int placementValue, int x, int y)
    {
        var placement = (ClipOverlayPlacement)placementValue;
        var target = new ClipOverlayTarget("DISPLAY2", new PixelRect(-1920, 0, 1920, 1920), new PixelRect(-1920, 0, 1920, 1920), 2, ClipOverlayTargetReason.Primary);
        Assert.Equal(new PixelPoint(x, y), ClipOverlayLayout.Position(target, placement, 400, 160));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnimationMovesInwardAndStaysInsideWorkArea(int placementValue)
    {
        var placement = (ClipOverlayPlacement)placementValue;
        var target = new ClipOverlayTarget("DISPLAY1", new PixelRect(-1920, -200, 1920, 1080), new PixelRect(-1900, -180, 1880, 1040), 2, ClipOverlayTargetReason.Primary);
        var final = ClipOverlayLayout.Position(target, placement, 400, 160);
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var start = ClipOverlayLayout.AnimatedPosition(target, placement, 400, 160, 0);
        var middle = ClipOverlayLayout.AnimatedPosition(target, placement, 400, 160, 0.5);

        Assert.Equal(final.X + (left ? 48 : -48), start.X);
        Assert.Equal(final.X + (left ? 24 : -24), middle.X);
        Assert.Equal(final, ClipOverlayLayout.AnimatedPosition(target, placement, 400, 160, 1));
        AssertContained(start, target.WorkArea, 400, 160);
        AssertContained(middle, target.WorkArea, 400, 160);
        AssertContained(final, target.WorkArea, 400, 160);
    }

    [Fact]
    public void UnknownPlacementFallsBackToTopRight()
        => Assert.Equal(ClipOverlayPlacement.TopRight, ClipOverlayPlacementParser.Parse("future value"));

    [Fact]
    public void SavedSoundUsesCapturedVolumeOnBackgroundThread()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var played = new ManualResetEventSlim();
        string? volume = null;
        var callerThread = Environment.CurrentManagedThreadId;
        var soundThread = callerThread;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, value =>
        {
            volume = value;
            soundThread = Environment.CurrentManagedThreadId;
            played.Set();
        }, () => _epoch);

        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 80, _epoch) with { SoundVolume = "High" });

        Assert.True(played.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal("High", volume);
        Assert.NotEqual(callerThread, soundThread);
    }

    [Fact]
    public void SavedEventWithoutSoundSnapshotDoesNotDispatchSound()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var sounds = 0;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => sounds++, () => _epoch, play => play());

        coordinator.Publish(Event(Guid.NewGuid(), 1, ClipOverlayKind.Saved, 80, _epoch) with { SoundVolume = null });

        Assert.Equal(0, sounds);
    }

    private static void AssertContained(PixelPoint position, PixelRect area, int width, int height)
    {
        Assert.InRange(position.X, area.X, area.Right - width);
        Assert.InRange(position.Y, area.Y, area.Bottom - height);
    }

    private ClipOverlayEvent Event(Guid id, int stage, ClipOverlayKind kind, int priority, DateTime requested) => new(
        id, stage, requested, _epoch, priority, kind,
        kind == ClipOverlayKind.Saved ? "Clip Saved" : kind.ToString(), null,
        new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary),
        ClipOverlayPlacement.TopRight, true,
        SoundVolume: kind == ClipOverlayKind.Saved ? "Medium" : null);

    private sealed class FakeSurface : IClipOverlaySurface
    {
        public List<ClipOverlayPresentation> Presentations { get; } = new();
        public List<long> Dismissals { get; } = new();
        private readonly List<(ClipOverlayPresentation Presentation, Action<ClipOverlayPresentationResult> Completion)> _pending = new();
        public bool AutoPresent { get; set; } = true;
        public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
        {
            Presentations.Add(presentation);
            _pending.Add((presentation, completion));
            if (AutoPresent) CompleteLast(true);
        }
        public void CompleteLast(bool presented)
        {
            var item = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            item.Completion(new ClipOverlayPresentationResult(item.Presentation.Generation, presented));
        }
        public void Complete(long generation, bool presented, string? reason = null)
        {
            var index = _pending.FindIndex(item => item.Presentation.Generation == generation);
            if (index < 0) return;
            var item = _pending[index];
            _pending.RemoveAt(index);
            item.Completion(new ClipOverlayPresentationResult(generation, presented, reason));
        }
        public void CompleteAny(Random random)
        {
            if (_pending.Count == 0) return;
            Complete(_pending[random.Next(_pending.Count)].Presentation.Generation, random.Next(4) > 0, "random");
        }
        public void CompleteAll() { while (_pending.Count > 0) Complete(_pending[0].Presentation.Generation, true); }
        public void Dismiss(long generation) => Dismissals.Add(generation);
        public void Dispose() { }
    }

    private sealed class FakeScheduler : IClipOverlayScheduler
    {
        private readonly List<Scheduled> _items = new();
        public List<TimeSpan> Delays { get; } = new();
        public IDisposable Schedule(TimeSpan delay, Action callback) { var item = new Scheduled(callback); _items.Add(item); Delays.Add(delay); return item; }
        public void Fire(int index) { var item = _items[index]; if (!item.Cancelled) item.Callback(); }
        public void FireAll() { for (var i = 0; i < _items.Count; i++) Fire(i); }
        // The most recent dwell still armed: the visible card's.
        public void FireLatestDwell()
        {
            for (var i = _items.Count - 1; i >= 0; i--)
                if (Delays[i] < TimeSpan.FromSeconds(5) && !_items[i].Cancelled) { _items[i].Cancel(); _items[i].Callback(); return; }
        }
        public void FireAny(Random random)
        {
            var live = Enumerable.Range(0, _items.Count).Where(i => !_items[i].Cancelled).ToArray();
            if (live.Length == 0) return;
            var item = _items[live[random.Next(live.Length)]];
            item.Cancel();
            item.Callback();
        }
        public void Dispose() { }
        private sealed class Scheduled(Action callback) : IDisposable
        {
            public Action Callback { get; } = callback;
            public bool Cancelled { get; private set; }
            public void Cancel() => Cancelled = true;
            public void Dispose() => Cancelled = true;
        }
    }
}
