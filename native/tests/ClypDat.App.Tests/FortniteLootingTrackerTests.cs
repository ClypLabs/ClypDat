using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FortniteLootingTrackerTests
{
    [Fact]
    public void AttributesToRecorderAfterLateIdentityAndCountsActorsOnce()
    {
        var tracker = new FortniteLootingTracker();
        tracker.UpdatePickup(100, collected: true);
        tracker.UpdatePickup(100, target: 10);
        tracker.UpdatePickup(100, collected: true, target: 10);
        tracker.UpdatePickup(101, collected: true, target: 20);
        tracker.UpdatePickup(102, collected: false, target: 10);
        Assert.Null(tracker.Count(true));
        tracker.RegisterPawn(20, 2000);
        tracker.RegisterPawn(10, 1000);
        tracker.SetRecorder(1000);
        Assert.Equal(1, tracker.Count(true));
    }

    [Fact]
    public void TracksRespawnedPawnsAndExcludesRecollectingOwnDrops()
    {
        var tracker = new FortniteLootingTracker();
        tracker.SetRecorder(1000);
        tracker.RegisterPawn(10, 1000);
        tracker.RegisterPawn(11, 1000);
        tracker.RegisterPawn(20, 2000);
        tracker.UpdatePickup(100, collected: true, target: 11, droppedBy: 10);
        tracker.UpdatePickup(101, collected: true, target: 11, droppedBy: 20);
        tracker.UpdatePickup(102, collected: true, target: 10, droppedBy: 10);
        Assert.Equal(1, tracker.Count(true));
    }

    [Fact]
    public void MissingOrIncompleteDataIsUnknownButVerifiedNoPickupsIsZero()
    {
        var tracker = new FortniteLootingTracker();
        tracker.SetRecorder(1000);
        tracker.RegisterPawn(10, 1000);
        Assert.Null(tracker.Count(true));
        tracker.UpdatePickup(100, collected: false);
        Assert.Equal(0, tracker.Count(true));
        Assert.Null(tracker.Count(false));
        tracker.UpdatePickup(100, collected: true);
        Assert.Null(tracker.Count(true));
        tracker.UpdatePickup(100, target: 10);
        Assert.Equal(1, tracker.Count(true));
        tracker.MarkIncomplete();
        Assert.Null(tracker.Count(true));
    }

    [Fact]
    public void LateDropperInformationCorrectsPreviouslyCollectedItems()
    {
        var tracker = new FortniteLootingTracker();
        tracker.SetRecorder(1000);
        tracker.RegisterPawn(10, 1000);
        tracker.UpdatePickup(100, collected: true, target: 10);
        Assert.Equal(1, tracker.Count(true));
        tracker.UpdatePickup(100, droppedBy: 10);
        Assert.Equal(0, tracker.Count(true));
    }

    [Fact]
    public void UnattributedRemoteCollectionsLeaveConfirmedCountsAsExplicitLowerBounds()
    {
        var tracker = new FortniteLootingTracker();
        tracker.SetRecorder(1000);
        tracker.RegisterPawn(10, 1000);
        tracker.UpdatePickup(100, collected: true);
        Assert.Null(tracker.Count(true));
        tracker.UpdatePickup(101, collected: true, target: 10);
        Assert.Equal(1, tracker.Count(true));
        Assert.True(tracker.HasUnattributedCollections);
        var player = new MatchPlayer { IsLocal = true, LootingPickups = 1, LootingPartial = true };
        var match = new MatchStats { ZeroBuild = true, Players = [player] };
        Assert.Equal("≥1", Assert.Single(FortniteStatsPresentation.RadarAxes(match), axis => axis.Label == "Looting").Display);
        Assert.Contains(FortniteStatsPresentation.LootingPartial, FortniteStatsPresentation.RadarNotes(match));
        Assert.Contains(FortniteStatsPresentation.MatchDetails(match), row => row.Label == "Items picked up" && row.Value == "≥1");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(0, 0.0)]
    [InlineData(24, 24.0)]
    [InlineData(100, 100.0)]
    [InlineData(150, 100.0)]
    public void RadarAndDetailsUseVerifiedCountsWithoutInventingMissingOnes(int? pickups, double? expected)
    {
        var player = new MatchPlayer { IsLocal = true, LootingPickups = pickups };
        var match = new MatchStats { ZeroBuild = true, Players = [player] };
        Assert.Equal(expected, FortniteStatsPresentation.LootingScore(player));
        var axis = Assert.Single(FortniteStatsPresentation.RadarAxes(match), axis => axis.Label == "Looting");
        Assert.Equal(expected is not null, axis.Available);
        Assert.Equal(expected is null, FortniteStatsPresentation.RadarNotes(match).Contains(FortniteStatsPresentation.LootingUnavailable));
        if (pickups is >= 0) Assert.Contains(FortniteStatsPresentation.MatchDetails(match), row => row.Label == "Items picked up" && row.Value == pickups.ToString());
    }
}
