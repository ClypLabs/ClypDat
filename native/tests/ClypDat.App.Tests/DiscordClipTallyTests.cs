using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DiscordClipTallyTests
{
    [Fact]
    public void CountsClipsForTheGameBeingPlayed()
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");

        tally.Record();
        tally.Record();

        Assert.Equal(2, tally.Count);
        Assert.Equal("2 clips saved", tally.Describe());
    }

    // The reported bug: one clip in a fresh game read as "3 clips saved"
    // because the count was never zeroed between titles.
    [Fact]
    public void SwitchingGameStartsFromZero()
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");
        tally.Record();
        tally.Record();

        tally.ObserveActivity("Fortnite");
        tally.Record();

        Assert.Equal(1, tally.Count);
        Assert.Equal("1 clip saved", tally.Describe());
    }

    // UpdateDiscordPresence runs on all sorts of unrelated changes - every
    // library rebuild, every settings toggle - so re-observing the same game
    // must not disturb the count.
    [Fact]
    public void ReobservingTheSameGameKeepsTheCount()
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");
        tally.Record();

        tally.ObserveActivity("Overwatch®");
        tally.ObserveActivity("Overwatch®");

        Assert.Equal(1, tally.Count);
    }

    // The trap that made keying off the activity "kind" wrong: kind is
    // "recording:<game>"/"playing:<game>", so it changes when the replay buffer
    // starts or stops for the same game. The tally sees only the game name, so
    // toggling recording cannot wipe it.
    [Fact]
    public void TogglingRecordingWithinAGameKeepsTheCount()
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");
        tally.Record();
        tally.Record();

        // What a stop/start looks like to the tally: the same game name again.
        tally.ObserveActivity("Overwatch®");

        Assert.Equal(2, tally.Count);
    }

    [Fact]
    public void ClosingAndRelaunchingTheSameGameStartsFromZero()
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");
        tally.Record();

        tally.ObserveActivity(null);
        tally.ObserveActivity("Overwatch®");

        Assert.Equal(0, tally.Count);
        Assert.Equal("Ready to clip", tally.Describe());
    }

    [Theory]
    [InlineData(0, "Ready to clip")]
    [InlineData(1, "1 clip saved")]
    [InlineData(2, "2 clips saved")]
    [InlineData(1234, "1,234 clips saved")]
    public void DescribesTheCountTheWayDiscordShowsIt(int clips, string expected)
    {
        var tally = new DiscordClipTally();
        tally.ObserveActivity("Overwatch®");
        for (var index = 0; index < clips; index++) tally.Record();

        Assert.Equal(expected, tally.Describe());
    }
}
