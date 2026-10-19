using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Cs2AutoClipWindowTests
{
    private static readonly DateTime Origin = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelectedKillKeepsTenSecondsOfLead()
    {
        var settings = new AutoClipGameSettings
        {
            Enabled = true,
            Events = new Dictionary<string, bool> { ["kill"] = true }
        };
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(1), Origin.AddSeconds(20));
        Assert.Empty(clips);
        listener.ProcessPayload(Payload(1, roundOver: true), Origin.AddSeconds(100));

        var clip = Assert.Single(clips);
        Assert.Equal(Origin.AddSeconds(10), clip.StartUtc);
        Assert.Equal(Origin.AddSeconds(30), clip.EndUtc);
    }

    [Theory]
    [InlineData("kill", 1)]
    [InlineData("2k", 2)]
    [InlineData("3k", 3)]
    [InlineData("4k", 4)]
    [InlineData("ace", 5)]
    public void OnlySelectedTierControlsWindowEvenWhenMoreKillsFollow(string selected, int tier)
    {
        var settings = Settings(selected);
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        for (var kills = 1; kills <= 5; kills++)
        {
            listener.ProcessPayload(Payload(kills), Origin.AddSeconds(kills * 20));
            Assert.Empty(clips);
        }
        listener.ProcessPayload(Payload(5, roundOver: true), Origin.AddSeconds(140));
        listener.ProcessPayload(Payload(5, roundOver: true), Origin.AddSeconds(141));

        var clip = Assert.Single(clips);
        Assert.Equal(selected, clip.EventId);
        Assert.Equal(Origin.AddSeconds(tier * 20 - 10), clip.StartUtc);
        Assert.Equal(Origin.AddSeconds(tier * 20 + 10), clip.EndUtc);
        Assert.Equal(selected, Assert.Single(clip.Events!).Id);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(3, true)]
    public void HigherSelectedTierReplacesEarlierCandidatesWithoutDuplicateClips(int tier, bool dies)
    {
        var settings = Settings("kill", "2k", "3k", "4k", "ace");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        for (var kills = 1; kills <= tier; kills++)
            listener.ProcessPayload(Payload(kills), Origin.AddSeconds(kills * 20));
        Assert.Empty(clips);
        listener.ProcessPayload(Payload(tier, roundOver: !dies, deaths: dies ? 1 : 0), Origin.AddSeconds(140));
        listener.ProcessPayload(Payload(tier, roundOver: true, deaths: dies ? 1 : 0), Origin.AddSeconds(145));

        var clip = Assert.Single(clips);
        var expected = tier == 5 ? "ace" : $"{tier}k";
        Assert.Equal(expected, clip.EventId);
        Assert.Equal(Origin.AddSeconds(tier * 20 - 10), clip.StartUtc);
        Assert.Equal(Origin.AddSeconds(tier * 20 + 10), clip.EndUtc);
        Assert.Equal(expected, Assert.Single(clip.Events!).Id);
    }

    [Fact]
    public void RoundBelowSelectedTierSavesNothing()
    {
        var settings = Settings("3k");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(2), Origin.AddSeconds(20));
        listener.ProcessPayload(Payload(2, roundOver: true), Origin.AddSeconds(140));
        Assert.Empty(clips);
    }

    [Fact]
    public void DisabledGameDiscardsPendingCandidateBeforeRoundEnds()
    {
        var settings = Settings("kill");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(1), Origin.AddSeconds(20));
        settings.Enabled = false;
        listener.ProcessPayload(Payload(1), Origin.AddSeconds(30));
        settings.Enabled = true;
        listener.ProcessPayload(Payload(1, roundOver: true), Origin.AddSeconds(140));
        Assert.Empty(clips);
    }

    [Fact]
    public void SpectatingFinalizesLocalTierWithoutCountingTeammateKills()
    {
        var settings = Settings("kill", "2k", "ace");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(2), Origin.AddSeconds(20));
        listener.ProcessPayload(Payload(5, playerId: "2"), Origin.AddSeconds(100));
        listener.ProcessPayload(Payload(5, roundOver: true, playerId: "2"), Origin.AddSeconds(140));
        var clip = Assert.Single(clips);
        Assert.Equal("2k", clip.EventId);
        Assert.Equal(Origin.AddSeconds(10), clip.StartUtc);
        Assert.Equal(Origin.AddSeconds(30), clip.EndUtc);
    }

    [Fact]
    public void ZeroHealthFinalizesCandidateWithoutRepeatedDeathClips()
    {
        var settings = Settings("kill", "death");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(1), Origin.AddSeconds(20));
        listener.ProcessPayload(Payload(1, health: 0), Origin.AddSeconds(80));
        listener.ProcessPayload(Payload(1, health: 0), Origin.AddSeconds(81));
        var clip = Assert.Single(clips);
        Assert.Equal("kill", clip.EventId);
        Assert.Equal("kill", Assert.Single(clip.Events!).Id);
    }

    [Fact]
    public void NewRoundFinalizesMissedRoundEndThenAllowsNextCandidate()
    {
        var settings = Settings("kill", "2k");
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);
        listener.ProcessPayload(Payload(0), Origin);
        listener.ProcessPayload(Payload(2), Origin.AddSeconds(20));
        listener.ProcessPayload(Payload(0, roundNumber: 2), Origin.AddSeconds(140));
        listener.ProcessPayload(Payload(1, roundNumber: 2), Origin.AddSeconds(150));
        listener.ProcessPayload(Payload(1, roundOver: true, roundNumber: 2), Origin.AddSeconds(160));
        Assert.Equal(new[] { "2k", "kill" }, clips.Select(clip => clip.EventId));
        Assert.Equal(Origin.AddSeconds(10), clips[0].StartUtc);
        Assert.Equal(Origin.AddSeconds(140), clips[1].StartUtc);
    }

    private static AutoClipGameSettings Settings(params string[] events) => new()
    {
        Enabled = true,
        Events = events.ToDictionary(id => id, _ => true, StringComparer.OrdinalIgnoreCase)
    };

    private static string Payload(int kills, bool roundOver = false, int deaths = 0,
        string playerId = "1", int health = 100, int roundNumber = 1) => $$"""
        {
          "auth": { "clypdat": "test-token" },
          "provider": { "steamid": "1" },
          "player": {
            "steamid": "{{playerId}}",
            "state": { "round_kills": {{kills}}, "round_killhs": 0, "health": {{health}} },
            "match_stats": { "deaths": {{deaths}}, "assists": 0 }
          },
          "map": { "name": "de_mirage", "mode": "competitive", "round": {{roundNumber}} },
          "round": { "phase": "{{(roundOver ? "over" : "live")}}" }
        }
        """;
}
