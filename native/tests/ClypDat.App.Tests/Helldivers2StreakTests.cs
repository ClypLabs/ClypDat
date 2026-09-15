using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Helldivers2StreakTests
{
    [Theory]
    [InlineData(19, null)]
    [InlineData(20, "killstreak-20")]
    [InlineData(49, "killstreak-20")]
    [InlineData(50, "killstreak-50")]
    [InlineData(99, "killstreak-50")]
    [InlineData(100, "killstreak-100")]
    [InlineData(123, "killstreak-100")]
    public void InclusiveBoundaries(int peak, string? expected)
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, peak);
        Present(detector, 0.5, peak);
        Absent(detector, 1);
        Absent(detector, 1.5);
        var events = Absent(detector, 2);
        if (expected is null) Assert.Empty(events);
        else
        {
            var item = Assert.Single(events);
            Assert.Equal(expected, item.EventId);
            Assert.Equal($"Killstreak ×{peak}", item.Label);
            Assert.Equal(TimeSpan.FromSeconds(1), item.Timestamp);
            Assert.Equal(TimeSpan.Zero, item.StreakStart);
        }
        Assert.Empty(Absent(detector, 3));
    }

    [Theory]
    [InlineData("killstreak-20,killstreak-50", "killstreak-50")]
    [InlineData("killstreak-20,killstreak-100", "killstreak-100")]
    [InlineData("killstreak-20", "killstreak-20")]
    [InlineData("", null)]
    public void HighestEnabledReachedTierWins(string enabled, string? expected)
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 125);
        Present(detector, 0.5, 125);
        Absent(detector, 1);
        Absent(detector, 1.5);
        var events = detector.Observe(Frame(2, Helldivers2CounterVisibility.Absent), enabled.Split(',').ToHashSet());
        if (expected is null) Assert.Empty(events);
        else
        {
            Assert.Equal(expected, Assert.Single(events).EventId);
            Assert.Equal("Killstreak ×125", events[0].Label);
        }
        Assert.Empty(Absent(detector, 3));
    }

    [Fact]
    public void BlanksDropsAndUnsupportedSpikesKeepConfirmedPeak()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, null);
        Present(detector, 1, 50);
        Present(detector, 2, 57);
        Present(detector, 3, null);
        Present(detector, 4, 57);
        Present(detector, 5, 568);
        Present(detector, 6, 8);
        for (var i = 7; i < 80; i++) Assert.Empty(Present(detector, i, null));
        Assert.Equal("Killstreak ×57", Complete(detector, 80).Label);
    }

    [Fact]
    public void UnconfirmedFinalHighDoesNotRaisePeak()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 50);
        Present(detector, 1, 50);
        Present(detector, 2, 568);
        Assert.Equal("Killstreak ×50", Complete(detector, 3).Label);
    }

    [Theory]
    [InlineData(Helldivers2CounterVisibility.Present)]
    [InlineData(Helldivers2CounterVisibility.Unknown)]
    public void InterruptedAbsenceRestartsConfirmation(Helldivers2CounterVisibility interruption)
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 20);
        Present(detector, 0.5, 20);
        Absent(detector, 1);
        Absent(detector, 1.5);
        Assert.Empty(detector.Observe(Frame(2, interruption)));
        Assert.Equal(TimeSpan.FromSeconds(3), Complete(detector, 3).Timestamp);
    }

    [Fact]
    public void AbsenceNeedsThreeDistinctSamplesAndOneSecond()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 20);
        Present(detector, 0.5, 20);
        Absent(detector, 1);
        Assert.Empty(Absent(detector, 1));
        Assert.Empty(Absent(detector, 1.1));
        Assert.Empty(Absent(detector, 1.2));
        Assert.Single(Absent(detector, 2));
    }

    [Fact]
    public void CaptureFailureCancelsAbsenceConfirmation()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 20);
        Present(detector, 0.5, 20);
        Absent(detector, 1);
        Absent(detector, 1.5);
        detector.ObserveCaptureFailure();
        Assert.Equal(TimeSpan.FromSeconds(3), Complete(detector, 3).Timestamp);
    }

    [Fact]
    public void SessionResetDiscardsPendingStreakAndAllowsNewTimeline()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 100, 57);
        Present(detector, 101, 57);
        Absent(detector, 102);
        detector.ResetSession();
        Assert.Empty(Absent(detector, 0));
        Assert.Empty(Absent(detector, 1));
        Assert.Empty(Absent(detector, 2));
        Present(detector, 3, 20);
        Present(detector, 4, 20);
        Assert.Equal("Killstreak ×20", Complete(detector, 5).Label);
    }

    [Fact]
    public void ConsecutiveStreaksCompleteSeparately()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 57);
        Present(detector, 0.5, 57);
        var first = Complete(detector, 1);
        Present(detector, 2.5, 20);
        Present(detector, 3, 20);
        var second = Complete(detector, 3.5);
        Assert.Equal("Killstreak ×57", first.Label);
        Assert.Equal("Killstreak ×20", second.Label);
        Assert.NotEqual(first.OccurrenceId, second.OccurrenceId);
        Assert.Equal(TimeSpan.FromSeconds(2.5), second.StreakStart);
    }

    [Fact]
    public void EliminationDoesNotDiscardVisibleStreak()
    {
        var detector = new Helldivers2Detector();
        Present(detector, 0, 57);
        Present(detector, 1, 57);
        Assert.Empty(detector.Observe(Frame(2, Helldivers2CounterVisibility.Present) with { CenterBannerText = "ELIMINATED" }));
        Assert.Equal("eliminated", Assert.Single(detector.Observe(Frame(3, Helldivers2CounterVisibility.Present)
            with { CenterBannerText = "ELIMINATED" })).EventId);
        Assert.Equal("Killstreak ×57", Complete(detector, 4).Label);
    }

    [Theory]
    [InlineData("X1000")]
    [InlineData("9999 KILLS")]
    [InlineData("k56")]
    [InlineData("")]
    public void ParserRejectsInvalidForms(string text) => Assert.False(Helldivers2Detector.TryParseKillCounter(text, out _));

    private static Helldivers2FrameObservation Frame(double seconds, Helldivers2CounterVisibility visibility, int? count = null) =>
        new(TimeSpan.FromSeconds(seconds), "", "", count.HasValue ? $"X{count}" : "", visibility, count);

    private static IReadOnlyList<Helldivers2DetectedEvent> Present(Helldivers2Detector detector, double seconds, int? count) =>
        detector.Observe(Frame(seconds, Helldivers2CounterVisibility.Present, count));

    private static IReadOnlyList<Helldivers2DetectedEvent> Absent(Helldivers2Detector detector, double seconds) =>
        detector.Observe(Frame(seconds, Helldivers2CounterVisibility.Absent));

    private static Helldivers2DetectedEvent Complete(Helldivers2Detector detector, double firstAbsent)
    {
        Assert.Empty(Absent(detector, firstAbsent));
        Assert.Empty(Absent(detector, firstAbsent + 0.5));
        return Assert.Single(Absent(detector, firstAbsent + 1));
    }
}
