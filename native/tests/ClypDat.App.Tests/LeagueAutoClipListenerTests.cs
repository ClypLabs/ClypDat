using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

// Drives the real listener against a stand-in for League's local game client,
// so neither auto-clips nor match presence need a live match to be checked.
public sealed class LeagueAutoClipListenerTests
{
    private sealed class FakeGameClient(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(responses.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static AutoClipGameSettings Clipping(bool enabled) => new()
    {
        Enabled = enabled,
        Events = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["kill"] = true, ["assist"] = true }
    };

    private static string RiotSample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "league")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", "league", "liveclientdata_sample.json"));
    }

    private static async Task<T> WithinSeconds<T>(TaskCompletionSource<T> source, int seconds = 5)
    {
        var finished = await Task.WhenAny(source.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(finished == source.Task, "The listener never reported.");
        return await source.Task;
    }

    [Theory]
    [InlineData("\"Riot Tuxedo\"", "Riot Tuxedo")]
    [InlineData("\"Me#OCE\"", "Me#OCE")]
    [InlineData("Me#OCE", "Me#OCE")]
    public void ActivePlayerName_DecodesTheJsonString(string body, string expected)
    {
        Assert.Equal(expected, LeagueAutoClipListener.ActivePlayerName(body));
    }

    [Fact]
    public async Task MatchPresence_FromRiotsPublishedSample()
    {
        var client = new FakeGameClient(new() { ["liveclientdata/allgamedata"] = RiotSample() });
        using var listener = new LeagueAutoClipListener(() => Clipping(false), client) { ReportMatchPresence = true };
        var reported = new TaskCompletionSource<GameMatchPresence?>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MatchPresence.Changed += (_, presence) => { if (presence is not null) reported.TrySetResult(presence); };

        listener.Start();
        var presence = (await WithinSeconds(reported))!;

        Assert.Equal("Annie · Summoner's Rift", presence.Details);
        Assert.Equal("0/0/0", presence.State);
        Assert.Equal("https://cdn.communitydragon.org/latest/champion/Annie/square", presence.SmallImageUrl);
        // The sample is taken at 0:00, before there is a clock to show.
        Assert.Null(presence.MatchStartedUtc);
    }

    [Theory]
    [InlineData("Me#OCE")]
    [InlineData("Me")]
    public async Task AutoClip_FiresForTheActivePlayersKill(string killerName)
    {
        var events = JsonSerializer.Serialize(new
        {
            Events = new object[]
            {
                new { EventID = 0, EventName = "GameStart", EventTime = 0.0 },
                new { EventID = 1, EventName = "ChampionKill", EventTime = 90.0, KillerName = killerName, VictimName = "Someone", Assisters = Array.Empty<string>() }
            }
        });
        var client = new FakeGameClient(new()
        {
            ["liveclientdata/activeplayername"] = "\"Me#OCE\"",
            ["liveclientdata/eventdata"] = events
        });
        using var listener = new LeagueAutoClipListener(() => Clipping(true), client);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.AutoClipPending += (_, message) => pending.TrySetResult(message);

        listener.Start();

        Assert.Contains("Enemy Slain", await WithinSeconds(pending));
    }

    [Fact]
    public async Task AutoClip_StaysQuietWhileClippingIsOff()
    {
        var client = new FakeGameClient(new()
        {
            ["liveclientdata/activeplayername"] = "\"Me#OCE\"",
            ["liveclientdata/eventdata"] = """{ "Events": [ { "EventID": 1, "EventName": "ChampionKill", "KillerName": "Me#OCE", "VictimName": "Someone" } ] }""",
            ["liveclientdata/allgamedata"] = RiotSample()
        });
        using var listener = new LeagueAutoClipListener(() => Clipping(false), client) { ReportMatchPresence = true };
        var fired = false;
        listener.AutoClipPending += (_, _) => fired = true;
        var reported = new TaskCompletionSource<GameMatchPresence?>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.MatchPresence.Changed += (_, presence) => { if (presence is not null) reported.TrySetResult(presence); };

        listener.Start();
        await WithinSeconds(reported);
        await Task.Delay(700);

        Assert.False(fired);
    }
}
