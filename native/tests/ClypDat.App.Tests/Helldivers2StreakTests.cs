using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Helldivers2StreakTests
{
    [Theory]
    [InlineData(19, null)]
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

    [Theory]
    [InlineData("X1000")]
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
