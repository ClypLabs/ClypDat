using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Helldivers2DetectorTests
{
    [Fact]
    public void GrowingCounterDoesNotClipAtThresholdCrossings()
    {
        var detector = new Helldivers2Detector();
        var counts = new[] { 19, 20, 20, 50, 50, 57, 57 };
        for (var i = 0; i < counts.Length; i++)
            Assert.Empty(Observe(detector, i, counter: $"X{counts[i]}"));
    }

    [Fact]
    public void SuccessfulMission_UsesSquadPayoutAsSoleSmoothedTrigger()
    {
        var detector = new Helldivers2Detector();

        Assert.Empty(Observe(detector, 0, mission: "MISSION COMPLETED"));
        Assert.Empty(Observe(detector, 0.5, mission: "MISSION COMPLETED SQUAD PAYOUT"));
        var detected = Assert.Single(Observe(detector, 1, mission: "SQUAD PAYOUT"));
        Assert.Equal("successful-mission", detected.EventId);
        Assert.Empty(Observe(detector, 1.5, mission: "SQUAD PAYOUT"));
    }

    [Fact]
    public void SuccessfulMission_CanFireAgainOnlyAfterScreenHasCleared()
    {
        var detector = new Helldivers2Detector();
        Observe(detector, 0, mission: "SQUAD PAYOUT");
        Assert.Single(Observe(detector, 0.5, mission: "SQUAD PAYOUT"));
        for (var index = 0; index < 20; index++) Observe(detector, 1 + index * 0.5);
        Assert.Empty(Observe(detector, 11, mission: "SQUAD PAYOUT"));
        Assert.Single(Observe(detector, 11.5, mission: "SQUAD PAYOUT"));
    }

    [Theory]
    [InlineData("×19", 19)]
    [InlineData("X 50", 50)]
    [InlineData("100 KILLS", 100)]
    public void KillCounterParser_AcceptsExpectedOcrForms(string text, int expected)
    {
        Assert.True(Helldivers2Detector.TryParseKillCounter(text, out var actual));
        Assert.Equal(expected, actual);
    }

    private static IReadOnlyList<Helldivers2DetectedEvent> Observe(
        Helldivers2Detector detector, double seconds, string center = "", string mission = "", string counter = "") =>
        detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(seconds), center, mission, counter, Helldivers2CounterVisibility.Present));
}
