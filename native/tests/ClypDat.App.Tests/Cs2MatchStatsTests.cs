using System.Net;
using System.Text;
using System.Text.Json;
using ClypDat.App.Services;
using ICSharpCode.SharpZipLib.BZip2;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Cs2MatchStatsTests
{
    private const string Local = "76561198000000001";
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- GSI tracker ----

    private static JsonElement Gsi(string phase, int round, string roundPhase, string? wins = null, string? bomb = null,
        int kills = 0, int hs = 0, int matchKills = 0, int deaths = 0, string team = "CT", string playerId = Local, int ct = 0, int t = 0)
    {
        var json = $$"""
        {
          "provider": { "steamid": "{{Local}}" },
          "map": { "name": "de_mirage", "mode": "competitive", "phase": "{{phase}}", "round": {{round}},
                   "team_ct": { "score": {{ct}} }, "team_t": { "score": {{t}} }
                   {{(wins is null ? "" : $", \"round_wins\": {{{wins}}}")}} },
          "round": { "phase": "{{roundPhase}}" {{(bomb is null ? "" : $", \"bomb\": \"{bomb}\"")}} },
          "player": { "steamid": "{{playerId}}", "name": "Arashii", "team": "{{team}}",
                      "state": { "round_kills": {{kills}}, "round_killhs": {{hs}} },
                      "match_stats": { "kills": {{matchKills}}, "deaths": {{deaths}}, "assists": 0, "mvps": 1, "score": 9 } }
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void TrackerBuildsRoundsKillsAndBombFromSnapshots()
    {
        var tracker = new Cs2MatchTracker();
        var now = T0;
        MatchStats? Feed(JsonElement payload) { now = now.AddSeconds(5); return tracker.Observe(payload, now, (now - T0).TotalSeconds); }

        Assert.Null(Feed(Gsi("warmup", 0, "live", kills: 3, matchKills: 3)));
        Assert.Null(Feed(Gsi("live", 0, "freezetime")));
        Assert.Null(Feed(Gsi("live", 0, "live", kills: 1, hs: 1, matchKills: 1)));
        Assert.Null(Feed(Gsi("live", 1, "over", "\"1\": \"ct_win_elimination\"", kills: 1, hs: 1, matchKills: 1, ct: 1)));
        Assert.Null(Feed(Gsi("live", 1, "freezetime", "\"1\": \"ct_win_elimination\"", matchKills: 1, ct: 1)));
        Assert.Null(Feed(Gsi("live", 1, "live", "\"1\": \"ct_win_elimination\"", bomb: "planted", matchKills: 1, ct: 1)));
        // Dead: the snapshot is a teammate's, but the map still counts.
        Assert.Null(Feed(Gsi("live", 1, "live", "\"1\": \"ct_win_elimination\"", bomb: "planted", playerId: "76561198000000002", ct: 1)));
        Assert.Null(Feed(Gsi("live", 2, "over", "\"1\": \"ct_win_elimination\", \"2\": \"t_win_bomb\"", bomb: "exploded", matchKills: 1, deaths: 1, ct: 1, t: 1)));
        var match = Feed(Gsi("gameover", 2, "over", "\"1\": \"ct_win_elimination\", \"2\": \"t_win_bomb\"", matchKills: 1, deaths: 1, ct: 1, t: 1));

        Assert.NotNull(match);
        Assert.Equal("de_mirage", match.Map);
        Assert.Equal(2, match.Rounds.Count);
        Assert.True(match.Rounds[0].LocalWon);
        Assert.Equal(1, match.Rounds[0].LocalKills);
        Assert.Equal(1, match.Rounds[0].LocalHeadshots);
        Assert.False(match.Rounds[1].LocalWon);
        Assert.True(match.Rounds[1].BombExploded);
        Assert.Equal("bomb", match.Rounds[1].Reason);
        var player = Assert.Single(match.Players);
        Assert.Equal((1, 1), (player.Kills, player.Deaths));
        Assert.Equal(100, player.HeadshotPercent);
        // Warmup's three kills are gone; the live kill is a headshot.
        var kill = Assert.Single(match.Events, e => e.Kind == "kill");
        Assert.True(kill.Headshot);
        Assert.Contains(match.Events, e => e.Kind == "death");
        Assert.Contains(match.Events, e => e.Kind == "bomb_planted");
        Assert.Contains(match.Events, e => e.Kind == "bomb_exploded");
        Assert.Equal(2, match.Events.Count(e => e.Kind == "round_end"));
        Assert.All(match.Events, e => Assert.NotNull(e.SharedSeconds));
        // Game over is reported once, not for every later snapshot.
        Assert.Null(Feed(Gsi("gameover", 2, "over", "\"1\": \"ct_win_elimination\", \"2\": \"t_win_bomb\"")));
    }

    [Fact]
    public void TrackerClosesMatchWhenGameGoesAway()
    {
        var tracker = new Cs2MatchTracker();
        tracker.Observe(Gsi("live", 0, "freezetime"), T0, 0);
        tracker.Observe(Gsi("live", 1, "over", "\"1\": \"t_win_elimination\""), T0.AddMinutes(2), 120);
        var match = tracker.Finish();
        Assert.NotNull(match);
        Assert.False(Assert.Single(match.Rounds).LocalWon);
        Assert.Null(tracker.Finish());
    }

    [Theory]
    [InlineData(5, 12, "CT", 20, "T")]
    [InlineData(15, 12, "CT", 20, "CT")]
    [InlineData(25, 12, "CT", 27, "CT")]
    [InlineData(28, 12, "CT", 28, "CT")]
    public void InferSideFollowsHalftimeAndOvertimeSwaps(int round, int halftime, string finalSide, int lastRound, string expected) =>
        Assert.Equal(expected, Cs2MatchTracker.InferSide(round, halftime, finalSide, lastRound));

    // ---- Demo maths ----

    [Fact]
    public void DemoAccumulatorCapsDamageAndCountsKastTradesAndUtility()
    {
        var stats = new Cs2DemoStatsAccumulator();
        stats.MatchStarted();
        foreach (var (key, side) in new[] { (Local, "CT"), ("2", "CT"), ("3", "T"), ("4", "T") })
            stats.PlayerSeen(key, "P" + key[^1], key == Local ? 76561198000000001UL : 0, side);

        stats.Hurt(Local, "CT", "3", "T", 30, 70, "ak47");
        stats.Hurt(Local, "CT", "3", "T", 120, 0, "awp");
        stats.PlayerDied(10, "3", "T", Local, "CT", null, null, false, true, Local);
        stats.Hurt("2", "CT", "4", "T", 40, 60, "hegrenade");
        stats.Hurt("4", "T", "4", "T", 10, 50, "world"); // self damage: no credit
        stats.PlayerDied(12, "2", "CT", "4", "T", "3", "T", true, false, Local);
        stats.PlayerDied(14, "4", "T", Local, "CT", null, null, false, false, Local);
        stats.RoundEnded(20, "CT", 8, [(Local, "CT"), ("2", "CT"), ("3", "T"), ("4", "T")]);

        var match = stats.Build("de_inferno", Local, 30);
        var me = match.Players.Single(p => p.IsLocal);
        Assert.Equal(100, me.Damage);
        Assert.Equal(2, me.Kills);
        Assert.Equal(1, me.HeadshotKills);
        Assert.Equal(1, me.EntryKills);
        Assert.Equal(100, me.Adr);
        Assert.Equal(50, me.HeadshotPercent);
        var teammate = match.Players.Single(p => p.SteamId is null && p.Name == "P2");
        Assert.Equal(40, teammate.UtilityDamage);
        Assert.Equal(1, teammate.KastRounds); // died, traded by the local player within 5 s
        var flasher = match.Players.Single(p => p.Name == "P3");
        Assert.Equal(1, flasher.FlashAssists);
        Assert.Equal(0, flasher.Assists);
        Assert.Equal(1, flasher.KastRounds); // died untraded, but the flash assist counts
        Assert.Equal(1, match.Teams.Single(t => t.Id == "CT").Score);
        Assert.True(Assert.Single(match.Rounds).LocalWon);
        Assert.Equal(2, match.Events.Count(e => e.Kind == "kill"));
    }

    [Fact]
    public void DemoAccumulatorIgnoresWarmupAndGameCommencing()
    {
        var stats = new Cs2DemoStatsAccumulator();
        stats.PlayerDied(1, "3", "T", Local, "CT", null, null, false, false, Local);
        stats.MatchStarted();
        stats.RoundEnded(2, null, 16, []);
        Assert.Empty(stats.Build(null, Local, 3).Rounds);
    }

    // ---- Presentation ----

    [Theory]
    [InlineData(40, 0)]
    [InlineData(80, 0.5)]
    [InlineData(200, 1)]
    [InlineData(double.NaN, 0)]
    public void RadarNormalisesAgainstFixedRanges(double adr, double expected) =>
        Assert.Equal(expected, MatchStatsPresentation.Normalise(adr, MatchStatsPresentation.DamageRange), 3);

    [Fact]
    public void RadarOnlyWithDemoData()
    {
        var player = new MatchPlayer { IsLocal = true, TeamId = "CT", Kills = 20, Deaths = 10, RoundsPlayed = 20, Damage = 1600, HeadshotKills = 10, UtilityDamage = 120 };
        var live = new MatchStats { Players = [player] };
        Assert.Empty(MatchStatsPresentation.RadarAxes(live));
        var axes = MatchStatsPresentation.RadarAxes(live with { HasDemoData = true });
        Assert.Equal(["Aim", "Damage", "Combat", "Survival", "Utility"], axes.Select(a => a.Label));
        Assert.Equal(0.5, axes[1].Value, 3); // 80 ADR
        Assert.Equal(0.75, axes[3].Value, 3); // 0.5 survival
    }

    [Theory]
    [InlineData(13, 12, true)]
    [InlineData(12, 12, false)]
    [InlineData(25, 12, true)]
    [InlineData(26, 12, false)]
    [InlineData(28, 12, true)]
    [InlineData(9, 8, true)]
    [InlineData(5, 0, false)]
    public void RoundStripGapsAtEachHalf(int round, int halftime, bool expected) =>
        Assert.Equal(expected, MatchStatsPresentation.StartsHalf(round, halftime));

    // ---- Clip mapping ----

    [Fact]
    public void SameBootMatchPlacesEventsExactlyThroughSourceWindow()
    {
        var match = new MatchStats
        {
            Game = "cs2", BootId = "boot", HasLiveData = true, StartedUtc = T0, EndedUtc = T0.AddMinutes(40),
            Events = [new MatchEvent { Kind = "kill", SharedSeconds = 1000.5, OccurredUtc = T0 }]
        };
        var clip = new ClipTimeline(T0, T0.AddSeconds(30), 30, new SpotifySourceWindow(990, 30, "boot"), true, []);
        var marker = Assert.Single(MatchClipMapping.Markers(match, clip));
        Assert.Equal(10.5, marker.OffsetSeconds, 3);
        Assert.Equal("kill", marker.EventId);
        Assert.Empty(MatchClipMapping.Markers(match, clip with { Source = new SpotifySourceWindow(990, 30, "other") }));
    }

    private static MatchStats DemoOnly(params double[] killSeconds) => new()
    {
        Game = "cs2", HasDemoData = true, StartedUtc = T0, EndedUtc = T0.AddMinutes(40),
        Events = killSeconds.Select(s => new MatchEvent { Kind = "kill", DemoSeconds = s })
            .Append(new MatchEvent { Kind = "round_end", Round = 4, Won = true, DemoSeconds = 612 }).ToArray()
    };

    [Fact]
    public void BackfilledClipAlignsDemoKillsToItsOwnMarkers()
    {
        var match = DemoOnly(300, 600, 604, 609, 900);
        // The clip's file time puts it two minutes off the demo's estimate.
        var start = T0.AddSeconds(595 + 120);
        var clip = new ClipTimeline(start, start.AddSeconds(20), 20, null, true,
            [new("kill", "Kill", 5), new("2k", "2K", 9), new("headshot", "Headshot", 9.05), new("3k", "3K", 14)]);
        var marker = Assert.Single(MatchClipMapping.Markers(match, clip)); // kills duplicate the clip's own
        Assert.Equal("round_win", marker.EventId);
        Assert.Equal(17, marker.OffsetSeconds, 3);
        Assert.Same(match, MatchClipMapping.FindMatch([match], clip));
    }

    [Fact]
    public void AmbiguousOrUnreliableClipsGetNoMarkers()
    {
        var start = T0.AddSeconds(595);
        var anchors = new ClipEventMarker[] { new("kill", "Kill", 5), new("2k", "2K", 9) };
        // Two kill pairs four seconds apart: either could be this clip.
        Assert.Empty(MatchClipMapping.Markers(DemoOnly(600, 604, 700, 704), new ClipTimeline(start, start.AddSeconds(20), 20, null, true, anchors)));
        // A single anchor is not enough for a demo-only match.
        Assert.Empty(MatchClipMapping.Markers(DemoOnly(600), new ClipTimeline(start, start.AddSeconds(20), 20, null, true, [new("kill", "Kill", 5)])));
        // A trimmed clip's timestamps do not describe its content.
        Assert.Empty(MatchClipMapping.Markers(DemoOnly(600, 604), new ClipTimeline(start, start.AddSeconds(20), 20, null, false, anchors)));
    }

    [Fact]
    public void ClipTimelineUsesFileTimeAsClipEndAndRecordingStart()
    {
        var created = new DateTimeOffset(T0);
        var clip = MatchClipMapping.Describe(new ClipInfo("Counter-Strike 2", null, CapturedAt: created), created, TimeSpan.FromSeconds(30), false, null);
        Assert.Equal(T0.AddSeconds(-30), clip.StartUtc);
        Assert.True(clip.TimestampReliable);
        var recording = MatchClipMapping.Describe(null, created, TimeSpan.FromMinutes(40), true, null);
        Assert.Equal(T0, recording.StartUtc);
        Assert.False(recording.TimestampReliable);
        Assert.True(MatchClipMapping.IsCs2("Counter-Strike 2", null));
        Assert.False(MatchClipMapping.IsCs2("Valorant", null));
    }

    [Fact]
    public void DemoMergesIntoGsiMatchOnlyWhenRoundsAgree()
    {
        var live = new MatchStats
        {
            Id = "cs2-live", Game = "cs2", Map = "de_mirage", HasLiveData = true, StartedUtc = T0, EndedUtc = T0.AddMinutes(30), LocalSteamId = Local,
            Rounds = [new MatchRound { Number = 1, WinnerSide = "CT", EndedUtc = T0.AddSeconds(200) }, new MatchRound { Number = 2, WinnerSide = "T", EndedUtc = T0.AddSeconds(400) }],
            Events = [new MatchEvent { Kind = "kill", OccurredUtc = T0.AddSeconds(150), SharedSeconds = 1 }]
        };
        var demo = new MatchStats
        {
            Game = "cs2", Map = "de_mirage", HasDemoData = true, DemoStatus = "parsed",
            Players = [new MatchPlayer { Name = "Arashii", SteamId = Local, TeamId = "T", RoundsPlayed = 2 }],
            Rounds = [new MatchRound { Number = 1, WinnerSide = "CT", DemoSeconds = 260 }, new MatchRound { Number = 2, WinnerSide = "T", DemoSeconds = 460 }]
        };
        Assert.True(MatchClipMapping.IsSameMatch(live, demo, T0.AddMinutes(-5)));
        Assert.False(MatchClipMapping.IsSameMatch(live, demo with { Map = "de_nuke" }, T0));
        Assert.False(MatchClipMapping.IsSameMatch(live, demo with { Rounds = [demo.Rounds[0] with { WinnerSide = "T" }, demo.Rounds[1] with { WinnerSide = "CT" }] }, T0));

        var merged = MatchClipMapping.Merge(live, demo);
        Assert.Equal("cs2-live", merged.Id);
        Assert.True(merged.HasLiveData && merged.HasDemoData);
        Assert.True(Assert.Single(merged.Players).IsLocal);
        Assert.Equal("T", merged.LocalTeamId);
        Assert.Same(live.Events, merged.Events);
        Assert.Equal(T0.AddSeconds(400), merged.Rounds[1].EndedUtc);
    }

    // ---- Formats, link storage ----

    [Theory]
    [InlineData("76561198000000001", true)]
    [InlineData(" 76561198000000001 ", true)]
    [InlineData("7656119800000000", false)]
    [InlineData("12345678901234567", false)]
    public void SteamIdFormat(string value, bool valid) => Assert.Equal(valid, Cs2MatchHistoryFormats.NormaliseSteamId(value) is not null);

    [Theory]
    [InlineData("abcd-efghi-jklm", "ABCD-EFGHI-JKLM")]
    [InlineData("ABCD-EFGH-JKLM", null)]
    [InlineData("ABCD EFGHI JKLM", null)]
    public void AuthCodeFormat(string value, string? expected) => Assert.Equal(expected, Cs2MatchHistoryFormats.NormaliseAuthCode(value));

    [Theory]
    [InlineData("CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ", true)]
    [InlineData("CSGO-abcde-fhijk-mnopq-rstuv-23456", true)]
    [InlineData("CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXY0", false)]
    [InlineData("CSGO-ABCDE-FGHIK-LMNOP-QRSTU-VWXYZ", false)]
    [InlineData("csgo-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ", false)]
    public void ShareCodeFormat(string value, bool valid) => Assert.Equal(valid, Cs2MatchHistoryFormats.NormaliseShareCode(value) is not null);

    [Fact]
    public void LinkIsEncryptedAtRestAndNeverPrinted()
    {
        var path = Path.Combine(Path.GetTempPath(), "clypdat-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var link = new Cs2MatchHistoryLink { SteamId = Local, AuthCode = "ABCD-EFGHI-JKLM", KnownCode = "CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ" };
            Assert.DoesNotContain("EFGHI", link.ToString());
            Assert.DoesNotContain(Local, link.ToString());
            Assert.Equal("JKLM", link.AuthCodeHint);
            Cs2MatchHistoryLinkStore.Save(link, path);
            var raw = File.ReadAllBytes(path);
            Assert.DoesNotContain("ABCD-EFGHI", Encoding.UTF8.GetString(raw));
            Assert.DoesNotContain("ABCD-EFGHI", Encoding.Unicode.GetString(raw));
            Assert.Equal("ABCD-EFGHI-JKLM", Cs2MatchHistoryLinkStore.Load(path)?.AuthCode);
            Cs2MatchHistoryLinkStore.Delete(path);
            Assert.Null(Cs2MatchHistoryLinkStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ---- API client ----

    private sealed class FakeHandler(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? RequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return response;
        }
    }

    [Fact]
    public async Task ShareCodesGoInHttpsJsonBodyWithBearer()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"codes":["CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ","bogus"],"complete":false}""");
        var client = new Cs2MatchHistoryClient(handler, new Uri("https://example.test/"));
        var page = await client.GetShareCodesAsync("token", Local, "ABCD-EFGHI-JKLM", "CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYA", CancellationToken.None);
        Assert.Equal(["CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ"], page.Codes);
        Assert.False(page.Complete);
        Assert.Equal("https://example.test/api/desktop/cs2/share-codes", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Contains("\"authCode\":\"ABCD-EFGHI-JKLM\"", handler.RequestBody);
        Assert.Throws<InvalidOperationException>(() => new Cs2MatchHistoryClient(handler, new Uri("http://example.test/")));
    }

    [Fact]
    public async Task FailuresMapFromCodeOnlyBodies()
    {
        async Task<Cs2MatchHistoryException> Fail(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) =>
            await Assert.ThrowsAsync<Cs2MatchHistoryException>(() => new Cs2MatchHistoryClient(new FakeHandler(status, body, configure), new Uri("https://example.test/"))
                .GetShareCodesAsync("t", Local, "ABCD-EFGHI-JKLM", "CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ", CancellationToken.None));

        Assert.Equal(Cs2MatchHistoryFailure.SteamRejected, (await Fail((HttpStatusCode)422, """{"code":"steam_rejected"}""")).Failure);
        var limited = await Fail(HttpStatusCode.TooManyRequests, """{"code":"rate_limited"}""",
            response => response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));
        Assert.Equal(Cs2MatchHistoryFailure.RateLimited, limited.Failure);
        Assert.Equal(TimeSpan.FromSeconds(120), limited.RetryAfter);
        Assert.Equal(Cs2MatchHistoryFailure.Unavailable, (await Fail(HttpStatusCode.ServiceUnavailable, """{"code":"steam_unavailable"}""")).Failure);
        Assert.Equal(Cs2MatchHistoryFailure.SignedOut, (await Fail(HttpStatusCode.Unauthorized, "")).Failure);
        Assert.DoesNotContain("ABCD", (await Fail((HttpStatusCode)422, """{"code":"steam_rejected"}""")).Message);
    }

    [Fact]
    public async Task UnknownMatchIsNullNotAnError()
    {
        var client = new Cs2MatchHistoryClient(new FakeHandler(HttpStatusCode.NotFound, """{"code":"match_not_found"}"""), new Uri("https://example.test/"));
        Assert.Null(await client.GetMatchAsync("t", "CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ", CancellationToken.None));
        var found = new Cs2MatchHistoryClient(new FakeHandler(HttpStatusCode.OK,
            """{"shareCode":"x","matchId":"3401285613734396143","reservationId":"1","tvPort":1,"matchTime":1729389428,"map":"de_mirage","demoUrl":null}"""), new Uri("https://example.test/"));
        var info = await found.GetMatchAsync("t", "CSGO-ABCDE-FGHJK-LMNOP-QRSTU-VWXYZ", CancellationToken.None);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1729389428), info!.MatchTime);
        Assert.Null(info.DemoUrl);
    }

    // ---- Demo download ----

    [Theory]
    [InlineData("http://replay183.valve.net/730/003401285613734396143_1729389428.dem.bz2", true)]
    [InlineData("http://replay1.valve.net/730/1_2.dem.bz2", true)]
    [InlineData("https://replay183.valve.net/730/003401285613734396143_1729389428.dem.bz2", false)]
    [InlineData("http://replay183.valve.net.evil.test/730/1_2.dem.bz2", false)]
    [InlineData("http://replay183.valve.net/730/1_2.dem.bz2?x=1", false)]
    [InlineData("http://replay183.valve.net/570/1_2.dem.bz2", false)]
    [InlineData("http://evil.test/replay183.valve.net/730/1_2.dem.bz2", false)]
    public void OnlyValveReplayUrlsAreDownloaded(string url, bool allowed) => Assert.Equal(allowed, Cs2DemoDownloader.IsAllowedUrl(url));

    [Fact]
    public void UnpackStreamsBzip2AndDeletesTheArchive()
    {
        Directory.CreateDirectory(Cs2DemoDownloader.TempFolder);
        var compressed = Path.Combine(Cs2DemoDownloader.TempFolder, "test-" + Guid.NewGuid().ToString("N") + ".dem.bz2");
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("PBDEMS2 ", 5000)));
        using (var file = File.Create(compressed))
        using (var bz = new BZip2OutputStream(file)) bz.Write(payload);
        var result = Cs2DemoDownloader.Unpack(compressed, CancellationToken.None);
        try
        {
            Assert.Equal(Cs2DemoDownloadOutcome.Ready, result.Outcome);
            Assert.False(File.Exists(compressed));
            Assert.Equal(payload, File.ReadAllBytes(result.DemoPath!));
        }
        finally { Cs2DemoDownloader.TryDelete(result.DemoPath); }
    }

    [Fact]
    public void CopyStopsAtTheCap()
    {
        using var destination = new MemoryStream();
        Assert.False(Cs2DemoDownloader.CopyCapped(new MemoryStream(new byte[200_000]), destination, 100_000, CancellationToken.None));
        Assert.True(destination.Length <= 100_000);
    }
}
