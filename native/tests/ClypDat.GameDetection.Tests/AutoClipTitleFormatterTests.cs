using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class AutoClipTitleFormatterTests
{
    [Fact]
    public void HighestPriorityEventIsSoleTitle()
    {
        var at = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        var title = AutoClipTitleFormatter.Format("cs2", new[]
        {
            new AutoClipEvent("kill", "Kill", at, 10),
            new AutoClipEvent("2k", "2K", at.AddSeconds(2), 20),
            new AutoClipEvent("headshot", "Headshot", at.AddSeconds(2), 15),
            new AutoClipEvent("death", "Death", at.AddSeconds(5)),
            new AutoClipEvent("assist", "Assist", at.AddSeconds(6))
        }, "Ancient");

        Assert.Equal("✌\u200D2K - Ancient", title);
    }

    [Fact]
    public void RepeatedEventsStillProduceOneTitle()
    {
        var at = DateTime.UnixEpoch;
        var title = AutoClipTitleFormatter.Format("cs2", new[]
        {
            new AutoClipEvent("headshot", "Headshot", at),
            new AutoClipEvent("headshot", "Headshot", at.AddSeconds(1)),
            new AutoClipEvent("death", "Death", at.AddSeconds(2))
        });

        Assert.Equal("🎯Headshot", title);
    }

    [Fact]
    public void HighestPriorityWinsAcrossDifferentGames()
    {
        var at = DateTime.UnixEpoch;
        var title = AutoClipTitleFormatter.Format("league", new[]
        {
            new AutoClipEvent("kill", "Enemy Slain", at, 10),
            new AutoClipEvent("triple", "Triple Kill", at.AddSeconds(1), 30),
            new AutoClipEvent("ace", "Ace", at.AddSeconds(2), 45),
            new AutoClipEvent("assist", "Assist", at.AddSeconds(3))
        }, "Summoner's Rift");

        Assert.Equal("👑Ace - Summoner's Rift", title);
    }

    [Fact]
    public void EqualPriorityUsesEarliestEvent()
    {
        var at = DateTime.UnixEpoch;
        var title = AutoClipTitleFormatter.Format("dota2", new[]
        {
            new AutoClipEvent("assist", "Assist", at.AddSeconds(2), 0),
            new AutoClipEvent("death", "Death", at, 0)
        });

        Assert.Equal("💀Death", title);
    }

    [Theory]
    [InlineData("dota2", "rampage", "Rampage", "👑Rampage")]
    [InlineData("dota2", "aegis-picked", "Aegis Picked Up", "🛡️Aegis Picked Up")]
    [InlineData("league", "dragon-kill", "Dragon Kill", "🐉Dragon Kill")]
    [InlineData("league", "turret", "Turret Destroyed", "🏰Turret Destroyed")]
    public void OtherGamesUseStableEmojiTemplates(string gameId, string eventId, string label, string expected)
    {
        var title = AutoClipTitleFormatter.Format(gameId, new[] { new AutoClipEvent(eventId, label, DateTime.UnixEpoch) });
        Assert.Equal(expected, title);
    }
}
