using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class Cs2GsiListenerTests
{
    [Fact]
    public void DeathmatchIsOffByDefault()
    {
        Assert.False(new AutoClipGameSettings().DeathmatchClipping);
    }

    [Fact]
    public void DeathmatchDoesNotClipWhenDisabled()
    {
        var settings = Settings();
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload("deathmatch", 0));
        listener.ProcessPayload(Payload("deathmatch", 3, roundOver: true));

        Assert.Empty(clips);
    }

    [Fact]
    public void DeathmatchClipsWhenEnabled()
    {
        var settings = Settings();
        settings.DeathmatchClipping = true;
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload("deathmatch", 0));
        listener.ProcessPayload(Payload("deathmatch", 3, roundOver: true));

        var clip = Assert.Single(clips);
        Assert.Equal("3k", clip.EventId);
    }

    [Fact]
    public void CompetitiveClipsWhenDeathmatchIsDisabled()
    {
        var settings = Settings();
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload("competitive", 0));
        listener.ProcessPayload(Payload("competitive", 3, roundOver: true));

        Assert.Single(clips);
    }

    [Fact]
    public void DisabledDeathmatchDoesNotCreateAClipAfterModeChange()
    {
        var settings = Settings();
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload("deathmatch", 0));
        listener.ProcessPayload(Payload("deathmatch", 3));
        listener.ProcessPayload(Payload("competitive", 3, roundOver: true));

        Assert.Empty(clips);
    }

    [Fact]
    public void TurningOffDeathmatchClippingDropsPendingClip()
    {
        var settings = Settings();
        settings.DeathmatchClipping = true;
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload("deathmatch", 0));
        listener.ProcessPayload(Payload("deathmatch", 3));
        settings.DeathmatchClipping = false;
        listener.ProcessPayload(Payload("deathmatch", 3, roundOver: true));

        Assert.Empty(clips);
    }

    [Fact]
    public void MissingMapModeKeepsExistingClippingBehavior()
    {
        var settings = Settings();
        using var listener = new Cs2GsiListener(() => settings);
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload(null, 0));
        listener.ProcessPayload(Payload(null, 3, roundOver: true));

        Assert.Single(clips);
    }

    private static AutoClipGameSettings Settings() => new()
    {
        Events = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["3k"] = true }
    };

    private static string Payload(string? mode, int roundKills, bool roundOver = false) => $$"""
        {
          "map": {
            "name": "de_dust2",
            {{(mode is null ? string.Empty : $"\"mode\": \"{mode}\",")}}
            "round": 1
          },
          "round": { "phase": "{{(roundOver ? "over" : "live")}}" },
          "player": {
            "state": { "round_kills": {{roundKills}}, "round_killhs": 0 },
            "match_stats": { "deaths": 0, "assists": 0 }
          }
        }
        """;
}
