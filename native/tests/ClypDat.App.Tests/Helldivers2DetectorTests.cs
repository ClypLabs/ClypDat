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

    private static IReadOnlyList<Helldivers2DetectedEvent> Observe(
        Helldivers2Detector detector, double seconds, string center = "", string mission = "", string counter = "") =>
        detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(seconds), center, mission, counter, Helldivers2CounterVisibility.Present));
}
