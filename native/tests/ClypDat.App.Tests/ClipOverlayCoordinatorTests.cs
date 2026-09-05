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
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => sounds++, () => _epoch);
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
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => { }, () => _epoch);
        var save = Guid.NewGuid();
        coordinator.Publish(Event(save, 0, ClipOverlayKind.Saving, 80, _epoch));
        var currentPrimary = new ClipOverlayTarget("DISPLAY3", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 1.5, ClipOverlayTargetReason.Primary);
        coordinator.Publish(Event(save, 1, ClipOverlayKind.Saved, 80, _epoch) with { Target = currentPrimary });

        Assert.Equal(currentPrimary, surface.Presentations[^1].Event.Target);
    }

    [Fact]
    public void PriorityLatestWins_WithoutQueue()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => { }, () => _epoch);
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Standalone, 30, _epoch.AddSeconds(1)));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch.AddSeconds(-1)));
        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Failure, 100, _epoch.AddSeconds(2)));
        Assert.Equal(2, surface.Presentations.Count);
    }

    [Fact]
    public void GameHintHasReadingTimeButSaveStillReplacesItImmediately()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler(); var sounds = 0;
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => sounds++, () => _epoch);
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
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => sounds++, () => _epoch);
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
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => { }, () => _epoch);
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
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => { }, () => _epoch);

        coordinator.Publish(Event(Guid.NewGuid(), 0, ClipOverlayKind.Saving, 80, _epoch));
        Assert.Equal([TimeSpan.FromSeconds(5)], scheduler.Delays);

        surface.CompleteLast(true);
        Assert.Equal(TimeSpan.FromSeconds(3), scheduler.Delays[^1]);
    }

    [Fact]
    public void WorkflowStageHistoryEvictsOldestAfterLimit()
    {
        var surface = new FakeSurface(); var scheduler = new FakeScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, () => { }, () => _epoch);
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

    private static void AssertContained(PixelPoint position, PixelRect area, int width, int height)
    {
        Assert.InRange(position.X, area.X, area.Right - width);
        Assert.InRange(position.Y, area.Y, area.Bottom - height);
    }

    private ClipOverlayEvent Event(Guid id, int stage, ClipOverlayKind kind, int priority, DateTime requested) => new(
        id, stage, requested, _epoch, priority, kind,
        kind == ClipOverlayKind.Saved ? "Clip Saved" : kind.ToString(), null,
        new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary),
        ClipOverlayPlacement.TopRight, true);

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
        public void Dispose() { }
        private sealed class Scheduled(Action callback) : IDisposable
        {
            public Action Callback { get; } = callback;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
    }
}
