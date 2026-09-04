using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Helldivers2DetectorTests
{
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

    [Fact]
    public void Eliminated_IsSmoothedAndResetsKillThresholds()
    {
        var detector = new Helldivers2Detector();
        Assert.Empty(Observe(detector, 0, counter: "X19"));
        Assert.Equal("killstreak-20", Assert.Single(Observe(detector, 0.5, counter: "X20")).EventId);
        Assert.Empty(Observe(detector, 1, center: "ELIMINATED"));
        Assert.Equal("eliminated", Assert.Single(Observe(detector, 1.5, center: "ELIMINATED", counter: "X100")).EventId);
        Assert.Equal("killstreak-20", Assert.Single(Observe(detector, 2, counter: "20 KILLS")).EventId);
    }

    [Fact]
    public void KillThresholds_FireOnceWhenCrossedAndResetWhenCounterDrops()
    {
        var detector = new Helldivers2Detector();
        Assert.Empty(Observe(detector, 0, counter: "X19"));
        Assert.Equal("killstreak-20", Assert.Single(Observe(detector, 1, counter: "X21")).EventId);
        Assert.Empty(Observe(detector, 2, counter: "X21"));
        Assert.Equal("killstreak-50", Assert.Single(Observe(detector, 3, counter: "X50")).EventId);
        Assert.Empty(Observe(detector, 4, counter: "X3"));
        Assert.Equal("killstreak-20", Assert.Single(Observe(detector, 5, counter: "X20")).EventId);
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
        detector.Observe(new Helldivers2FrameObservation(TimeSpan.FromSeconds(seconds), center, mission, counter));
}
