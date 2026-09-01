using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Cs2AutoClipDiagnosticsTests
{
    [Fact]
    public void AuthorizedThreeKillRound_FinalizesOneClipAndRecordsEachStage()
    {
        var settings = new AutoClipGameSettings
        {
            Enabled = true,
            Events = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["3k"] = true }
        };
        using var listener = new Cs2GsiListener(() => settings, "test-token");
        var clips = new List<Cs2AutoClipRequest>();
        listener.AutoClipReady += (_, request) => clips.Add(request);

        listener.ProcessPayload(Payload(0));
        listener.ProcessPayload(Payload(1));
        listener.ProcessPayload(Payload(2));
        listener.ProcessPayload(Payload(3));
        listener.ProcessPayload(Payload(3, roundOver: true));

        var health = listener.GetHealthSnapshot();
        var clip = Assert.Single(clips);
        Assert.Equal("3K", clip.EventType);
        Assert.Equal(5, health.AcceptedPayloads);
        Assert.Equal(1, health.PendingEvents);
        Assert.Equal(1, health.FinalizedEvents);
        Assert.Equal("Event finalized", health.LastStage);
    }

    [Fact]
    public void UnauthorizedPayload_IsCountedWithoutExposingToken()
    {
        var settings = new AutoClipGameSettings { Enabled = true };
        using var listener = new Cs2GsiListener(() => settings, "expected-token");

        listener.ProcessPayload(Payload(0, token: "wrong-token"));

        var health = listener.GetHealthSnapshot();
        Assert.Equal(1, health.UnauthorizedPayloads);
        Assert.Equal(0, health.AcceptedPayloads);
        Assert.DoesNotContain("expected-token", JsonSerializer.Serialize(health), StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-token", JsonSerializer.Serialize(health), StringComparison.Ordinal);
    }

    private static string Payload(int roundKills, bool roundOver = false, string token = "test-token") => $$"""
        {
          "auth": { "clypdat": "{{token}}" },
          "provider": { "steamid": "1" },
          "player": {
            "steamid": "1",
            "state": { "round_kills": {{roundKills}}, "round_killhs": 0 },
            "match_stats": { "deaths": 0, "assists": 0 }
          },
          "map": { "name": "de_mirage", "mode": "competitive", "round": 1 },
          "round": { "phase": "{{(roundOver ? "over" : "live")}}" }
        }
        """;
}
