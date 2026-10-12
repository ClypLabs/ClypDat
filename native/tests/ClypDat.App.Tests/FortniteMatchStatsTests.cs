using System.Text;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FortniteMatchStatsTests
{
    private const string Me = "0B90E0B1B87747A78A731FEFB03AF756";
    private const string Them = "1423017BDDE54902907BA9774F4604E3";
    private static readonly DateTime StartUtc = new(2026, 10, 10, 8, 16, 3, DateTimeKind.Utc);

    // ---- A replay file, built the way Unreal's local replay streamer writes one ----

    private sealed record Elim(double Seconds, string Eliminator, string Eliminated, bool Knocked);

    private static byte[] Replay(bool live, DateTime localStart, uint lengthMs, IEnumerable<Elim>? eliminations = null,
        (int Elims, float Accuracy, int Damage)? stats = null, (int Place, int Players)? team = null, int dataChunks = 0)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII);
        writer.Write(0x1CA2E27Fu);
        writer.Write(6u); // HISTORY_ENCRYPTION: no custom versions
        writer.Write(lengthMs);
        writer.Write(0u);
        writer.Write(0u);
        FString(writer, "Unsaved Replay");
        writer.Write(live ? 1u : 0u);
        writer.Write(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified).ToBinary());
        writer.Write(0u); // not compressed
        writer.Write(0u); // not encrypted
        writer.Write(0u); // no key
        for (var i = 0; i < dataChunks; i++) Chunk(writer, 1, new byte[64]);
        foreach (var e in eliminations ?? [])
            Event(writer, "playerElim", "", (uint)(e.Seconds * 1000), body =>
            {
                body.Write(2); // version: ids as strings
                body.Write(new byte[8]);
                FString(body, e.Eliminated);
                FString(body, e.Eliminator);
                body.Write((byte)3);
                body.Write(e.Knocked ? 1u : 0u);
            });
        if (stats is { } s)
            Event(writer, "AthenaMatchStats", "AthenaMatchStats", lengthMs - 2000, body =>
            {
                body.Write(0u);
                body.Write(s.Accuracy);
                body.Write(2u); // assists
                body.Write((uint)s.Elims);
                body.Write((uint)s.Damage - 100); // weapon damage
                body.Write(100u); // other damage
                body.Write(1u); // revives
                body.Write(450u); // taken
                body.Write(900u); // structures
                body.Write(0u);
                body.Write(0u);
                body.Write(452100u); // centimetres
            });
        if (team is { } t)
            Event(writer, "AthenaMatchTeamStats", "AthenaMatchTeamStats", lengthMs - 2000, body =>
            {
                body.Write(0u);
                body.Write((uint)t.Place);
                body.Write((uint)t.Players);
            });
        writer.Flush();
        return output.ToArray();
    }

    private static void FString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length + 1);
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void Chunk(BinaryWriter writer, uint type, byte[] body)
    {
        writer.Write(type);
        writer.Write(body.Length);
        writer.Write(body);
    }

    private static void Event(BinaryWriter writer, string group, string metadata, uint startMs, Action<BinaryWriter> write)
    {
        using var data = new MemoryStream();
        using (var body = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true)) write(body);
        using var chunk = new MemoryStream();
        using (var header = new BinaryWriter(chunk, Encoding.ASCII, leaveOpen: true))
        {
            FString(header, "event-" + startMs);
            FString(header, group);
            FString(header, metadata);
            header.Write(startMs);
            header.Write(startMs);
            header.Write((int)data.Length);
            header.Write(data.ToArray());
        }
        Chunk(writer, 3, chunk.ToArray());
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("clypdat-fortnite-test-").FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static readonly Elim[] Match =
    [
        new(100, Them, "Bot", false),
        new(120, Me, Them, true),
        new(125, Me, Them, false),
        new(150, "X1", "X2", false),
        new(150, "X3", "X4", false),
        new(160, "X5", "X2", false), // X2 was rebooted: out only from 160
        new(163, Me, "Bot", false),
        new(200, "X6", Me, true),
        new(210, "X6", Me, false)
    ];

    private static FortniteReplayData Data(IReadOnlyList<Elim>? eliminations = null, int? place = 12, int? players = 10, string? playlist = "Playlist_NoBuildBR_Solo",
        bool stats = true, bool observedCounts = true) =>
        new(new FortniteReplayHeader("C:/r/UnsavedReplay-2026.10.10-19.16.03.replay", 1, StartUtc, StartUtc, 300, false, false, 0),
            stats ? new FortniteStatsBlock(2, 1, 0.38, 900, 300, 400, 0, 0, 452100, 0, 212) : null,
            place, players,
            (eliminations ?? Match).Select(e => new FortniteElimination(e.Seconds, e.Eliminator, e.Eliminated, e.Knocked, e.Eliminated == "Bot")).ToArray(),
            playlist, 0)
        {
            // Independent GameState samples, not reconstructed from kills.
            PlayersLeft = observedCounts ? [new(0, 10), new(100, 9), new(125, 8), new(150, 7), new(160, 6), new(163, 5), new(210, 4)] : [],
            PlayersLeftComplete = observedCounts
        };

    // ---- File access ----

    [Fact]
    public void HeaderReadsLiveFlagLengthAndLocalStartTime()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "UnsavedReplay-2026.10.10-19.16.03.replay");
        var local = new DateTime(2026, 10, 10, 19, 16, 3, 2);
        File.WriteAllBytes(path, Replay(live: true, local, 1_112_533));

        var header = FortniteReplayFile.TryReadHeader(path);
        Assert.NotNull(header);
        Assert.True(header.IsLive);
        Assert.Equal(1112.533, header.LengthSeconds, 3);
        Assert.Equal(FortniteReplayFile.LocalToUtc(local), header.StartUtc);
        Assert.Contains("UnsavedReplay-2026.10.10-19.16.03.replay|", header.CacheKey);
        Assert.Null(FortniteReplayFile.TryReadHeader(System.IO.Path.Combine(folder.Path, "missing.replay")));
        File.WriteAllBytes(path + ".bad", [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Null(FortniteReplayFile.TryReadHeader(path + ".bad"));
    }

    [Fact]
    public void HeaderTimeConvertsWithTheZoneRulesOfItsOwnDate()
    {
        // Sydney moved from +10 to +11 on 4 October 2026; replays either side
        // must both land on the right UTC instant.
        var sydney = TimeZoneInfo.CreateCustomTimeZone("Test/Sydney", TimeSpan.FromHours(10), "Sydney", "AEST", "AEDT",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 4),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 4, 5))
        ]);
        Assert.Equal(new DateTime(2026, 9, 28, 3, 20, 50, DateTimeKind.Utc), FortniteReplayFile.LocalToUtc(new DateTime(2026, 9, 28, 13, 20, 50), sydney));
        Assert.Equal(new DateTime(2026, 10, 10, 8, 16, 3, DateTimeKind.Utc), FortniteReplayFile.LocalToUtc(new DateTime(2026, 10, 10, 19, 16, 3), sydney));
    }

    [Fact]
    public void ParsingKeepsOnlyHeaderAndEventChunks()
    {
        var bytes = Replay(false, new DateTime(2026, 10, 10, 19, 16, 3), 300_000, Match, (9, 0.38f, 3702), (3, 96), dataChunks: 5);
        using var source = new MemoryStream(bytes);
        using var events = FortniteReplayFile.ReadForParsing(source, dataChunks: 0, includeEvents: true);
        using var oneChunk = FortniteReplayFile.ReadForParsing(source, dataChunks: 1, includeEvents: false);
        Assert.Equal(bytes.Length - 5 * (8 + 64), events.Length);
        Assert.True(oneChunk.Length < 300);
    }

    [Fact]
    public void ParsesStatsPlacementAndEliminationsFromAFinishedReplay()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "UnsavedReplay-2026.10.10-19.16.03.replay");
        File.WriteAllBytes(path, Replay(false, new DateTime(2026, 10, 10, 19, 16, 3), 300_000, Match, (9, 0.38f, 3702), (3, 96)));
        var header = FortniteReplayFile.TryReadHeader(path)!;

        var data = FortniteReplayParser.Parse(header, readPlaylist: true);

        Assert.Equal(9, data.Stats!.Eliminations);
        Assert.Equal(3702, data.Stats.DamageToPlayers);
        Assert.Equal(0.38, data.Stats.Accuracy, 3);
        Assert.Equal(298, data.Stats.Seconds, 3);
        Assert.Equal(3, data.Placement);
        Assert.Equal(96, data.TotalPlayers);
        Assert.Equal(Match.Length, data.Eliminations.Count);
        Assert.Equal(125, data.Eliminations[2].Seconds, 3);
        Assert.Equal(Me, data.Eliminations[2].Eliminator);
        Assert.False(data.Eliminations[2].Knocked);
        Assert.True(data.Eliminations[1].Knocked);
        Assert.Null(data.Playlist); // no network chunk to read it from
        Assert.Equal(0, data.SkippedEvents);
    }

    [Fact]
    public void AReplayIsOnlyEverOpenedForReading()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "UnsavedReplay-2026.10.10-19.16.03.replay");
        File.WriteAllBytes(path, Replay(false, new DateTime(2026, 10, 10, 19, 16, 3), 300_000, Match, (9, 0.38f, 3702), (3, 96)));
        var before = (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, Convert.ToHexString(File.ReadAllBytes(path)));
        // Fortnite may hold it open for writing, or delete it, mid-read.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var header = FortniteReplayFile.TryReadHeader(path)!;
            FortniteReplayParser.Parse(header, readPlaylist: true);
        }
        Assert.Equal(before, (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, Convert.ToHexString(File.ReadAllBytes(path))));
    }

    // ---- Owner, players left, the summary ----

    [Fact]
    public void OwnerIsTheLocalAccountInTheReplay()
    {
        Assert.Equal(Me, FortniteMatchStats.ResolveOwner(Data(), [Me]));
        Assert.Equal(Me, FortniteMatchStats.ResolveOwner(Data(), ["FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", Me]));
        // Never fought: with one account on the PC it is still that one.
        Assert.Equal(Me, FortniteMatchStats.ResolveOwner(Data([]), [Me]));
        Assert.Null(FortniteMatchStats.ResolveOwner(Data([]), [Me, Them]));
        Assert.Null(FortniteMatchStats.ResolveOwner(Data(), []));
    }

    [Fact]
    public void PlayersLeftRequiresObservedNetworkCounters()
    {
        var data = Data();
        var steps = FortniteMatchStats.Build(data, Me, null).PlayersLeft;
        Assert.Equal(data.PlayersLeft, steps);
        Assert.Equal(5, FortniteMatchStats.PlayersLeftBefore(steps, 10, 210, inclusive: false));
        Assert.Equal(4, FortniteMatchStats.PlayersLeftBefore(steps, 10, 210, inclusive: true));
        var unobserved = FortniteMatchStats.Build(Data(observedCounts: false), Me, null);
        Assert.Empty(unobserved.PlayersLeft);
        Assert.Null(unobserved.PlayersLeftAtEnd);
        Assert.Empty(FortniteStatsPresentation.PlacementSummary(unobserved));
    }

    [Fact]
    public void SummaryHoldsYourEventsPlacementAndTotals()
    {
        var match = FortniteMatchStats.Build(Data(), Me, "Arashii");

        Assert.Equal("fortnite-UnsavedReplay-2026.10.10-19.16.03", match.Id);
        Assert.Equal("fortnite", match.Game);
        Assert.Equal("Battle Royale · Zero Builds · Solo", match.Mode);
        Assert.True(match.ZeroBuild);
        Assert.Equal(12, match.Placement);
        Assert.False(match.Won);
        Assert.Equal(210, match.LocalEndSeconds);
        Assert.Equal(5, match.PlayersLeftAtEnd);
        Assert.Equal(["knock", "kill", "kill", "knocked", "death"], match.Events.Select(e => e.Kind));
        Assert.True(match.Events[2].Bot);
        Assert.True(match.Events[^1].Final);
        Assert.Equal(StartUtc.AddSeconds(120), match.Events[0].OccurredUtc);
        var player = match.LocalPlayer!;
        Assert.Equal("Arashii", player.Name);
        Assert.Equal(2, player.Kills); // the replay's own stats, not the event count
        Assert.Equal(1, player.Knocks);
        Assert.Equal(900, player.Damage);
        Assert.Equal(38, player.AccuracyPercent!.Value, 3);
        Assert.Equal(4521, player.DistanceMetres!.Value, 3);
    }

    [Fact]
    public void AWinEndsWithVictoryRoyale()
    {
        var eliminations = Match.Where(e => e.Eliminated != Me).ToArray();
        var match = FortniteMatchStats.Build(Data(eliminations, place: 1), Me, null);
        Assert.True(match.Won);
        Assert.Equal("victory", match.Events[^1].Kind);
        Assert.Equal(212, match.LocalEndSeconds);
        Assert.Equal("You", match.LocalPlayerName);
        Assert.Equal("Players left 10 → Victory Royale", FortniteStatsPresentation.PlacementSummary(match));
    }

    [Theory]
    [InlineData("Playlist_NoBuildBR_Trio", true, "Battle Royale · Zero Builds · Trios")]
    [InlineData("Playlist_FigmentNoBuildSquad", true, "Fortnite OG · Zero Builds · Squads")]
    [InlineData("Playlist_DefaultDuo", false, "Battle Royale · Builds · Duos")]
    [InlineData("Playlist_Mash_Squads_Legacy", null, "Horde Rush · Squads")]
    [InlineData("Playlist_PiperBootNoBuildSquad", true, "Reload · Zero Builds · Squads")]
    [InlineData("Playlist_RopeSmileDuo", false, "Reload · Builds · Duos")]
    [InlineData("Playlist_ForbiddenFruitOldNoBuildBRStack", true, "Blitz Royale · Zero Builds · Six Stack")]
    [InlineData(null, null, "Fortnite")]
    public void ZeroBuildAndModeComeFromThePlaylistOnly(string? playlist, bool? zeroBuild, string label)
    {
        Assert.Equal(zeroBuild, FortniteMatchStats.IsZeroBuild(playlist));
        Assert.Equal(label, FortniteMatchStats.ModeLabel(playlist));
    }

    [Fact]
    public void ExistingSavedModeLabelsUseTheRecordedPlaylistWithoutReparsing()
    {
        var match = FortniteMatchStats.Build(Data(playlist: "Playlist_NoBuildBR_Squad"), Me, null)
            with { Mode = "Zero Build · Squads" };
        Assert.Equal("Battle Royale · Zero Builds · Squads", MatchStatsViewModel.FromFortnite(match, null, string.Empty).MatchLabel);
    }

    // ---- Radar ----

    [Fact]
    public void ReplayPickupCountsReachTheSavedLocalPlayerAndRadar()
    {
        var match = FortniteMatchStats.Build(Data() with { LootingPickups = 24 }, Me, null);
        Assert.Equal(24, match.LocalPlayer!.LootingPickups);
        var looting = Assert.Single(FortniteStatsPresentation.RadarAxes(match), axis => axis.Label == "Looting");
        Assert.True(looting.Available);
        Assert.Equal(0.24, looting.Value, 3);
        Assert.DoesNotContain(FortniteStatsPresentation.LootingUnavailable, FortniteStatsPresentation.RadarNotes(match));
    }

    [Fact]
    public void ZeroBuildRadarIsADiamondWithoutBuilding()
    {
        var axes = FortniteStatsPresentation.RadarAxes(FortniteMatchStats.Build(Data(), Me, null));
        Assert.Equal(["Survival", "Combat", "Looting", "Aim"], axes.Select(axis => axis.Label));
        var looting = Assert.Single(axes, axis => axis.Label == "Looting");
        Assert.False(looting.Available);
        Assert.True(double.IsNaN(looting.Value));
        Assert.Equal("Not recorded", looting.Display);
    }

    [Fact]
    public void BuildModeRadarHasFiveAxesAndNeverInventsBuildingOrLooting()
    {
        var match = FortniteMatchStats.Build(Data(playlist: "Playlist_DefaultSquad"), Me, null);
        var axes = FortniteStatsPresentation.RadarAxes(match);
        Assert.Equal(["Survival", "Combat", "Building", "Looting", "Aim"], axes.Select(axis => axis.Label));
        Assert.Equal([true, true, false, false, true], axes.Select(axis => axis.Available));
        Assert.Contains(FortniteStatsPresentation.BuildingUnavailable, FortniteStatsPresentation.RadarNotes(match));
    }

    [Fact]
    public void UnknownPlaylistKeepsBuildingAndSaysSo()
    {
        var match = FortniteMatchStats.Build(Data(playlist: null), Me, null);
        Assert.Equal(5, FortniteStatsPresentation.RadarAxes(match).Count);
        Assert.Contains(FortniteStatsPresentation.BuildModeUnknown, FortniteStatsPresentation.RadarNotes(match));
    }

    [Fact]
    public void RadarScoresFollowTheDocumentedFormulas()
    {
        var match = FortniteMatchStats.Build(Data(), Me, null);
        var player = match.LocalPlayer!;
        // Combat: 50% x 2/10 elims + 50% x 900/3000 damage = 25.
        Assert.Equal(25, FortniteStatsPresentation.CombatScore(player)!.Value, 3);
        // Aim: (38 - 15) / (55 - 15) = 57.5.
        Assert.Equal(57.5, FortniteStatsPresentation.AimScore(player)!.Value, 3);
        // Survival uses fixed placement and time ranges, not recording length.
        Assert.Equal(100 * (0.6 * 88 / 99.0 + 0.4 * 210 / 1200), FortniteStatsPresentation.SurvivalScore(match)!.Value, 3);
        // Missing data is unavailable, never 0.
        var bare = FortniteMatchStats.Build(Data(place: null, players: null, stats: false), Me, null);
        Assert.Null(FortniteStatsPresentation.CombatScore(bare.LocalPlayer!));
        Assert.Null(FortniteStatsPresentation.AimScore(bare.LocalPlayer!));
        Assert.Null(FortniteStatsPresentation.SurvivalScore(bare));
        Assert.All(FortniteStatsPresentation.RadarAxes(bare), axis => Assert.False(axis.Available));
    }

    // ---- Lining the clip up with the replay ----

    private static ClipTimeline ClipAt(double fromSeconds, double toSeconds, double mediaSeconds) =>
        MatchClipMapping.Describe(new ClipInfo("Fortnite", null, CaptureStartUtc: StartUtc.AddSeconds(fromSeconds), CaptureEndUtc: StartUtc.AddSeconds(toSeconds),
            IsTrimmed: true, CaptureTimingVerified: true), DateTimeOffset.MinValue, TimeSpan.FromSeconds(mediaSeconds), false, null);

    [Fact]
    public void AnEventAt2m43IsThirteenSecondsIntoAClipFrom2m30()
    {
        var match = FortniteMatchStats.Build(Data([new Elim(163, Me, Them, false)]), Me, null);
        var clip = ClipAt(150, 180, 30);
        Assert.True(clip.Exact);
        var window = MatchClipMapping.ReplayWindow(match, clip)!;

        var marker = Assert.Single(MatchClipMapping.Markers(match, clip));
        Assert.Equal(13, marker.OffsetSeconds, 3);
        Assert.Equal("Elimination", marker.EventLabel);
        var row = Assert.Single(FortniteStatsPresentation.Timeline(match, window));
        Assert.Equal("2:43", row.Time);
        Assert.Equal("0:13", row.ClipTime);
        Assert.True(row.InClip);
        Assert.Equal(["1", "0", "—", "—"], FortniteStatsPresentation.ClipTiles(match, window).Select(tile => tile.Value));
    }

    [Fact]
    public void ASpedUpExportStillLinesUp()
    {
        var match = FortniteMatchStats.Build(Data([new Elim(163, Me, Them, false)]), Me, null);
        // 30 s of game in a 15 s file.
        var marker = Assert.Single(MatchClipMapping.Markers(match, ClipAt(150, 180, 15)));
        Assert.Equal(6.5, marker.OffsetSeconds, 3);
    }

    [Fact]
    public void ATrimmedClipWithoutCaptureTimesIsNotSliced()
    {
        var match = FortniteMatchStats.Build(Data(), Me, null);
        var clip = MatchClipMapping.Describe(new ClipInfo("Fortnite", null, CapturedAt: StartUtc.AddSeconds(180), IsTrimmed: true),
            DateTimeOffset.MinValue, TimeSpan.FromSeconds(30), false, null);
        Assert.False(clip.TimestampReliable);
        Assert.Null(MatchClipMapping.ReplayWindow(match, clip));
        Assert.Empty(MatchClipMapping.Markers(match, clip));
        var view = MatchStatsViewModel.FromFortnite(match, null, "note");
        Assert.False(view.HasScopeToggle);
        Assert.True(view.IsFullScope);
    }

    [Fact]
    public void ClipScopeIsTheDefaultInsideTheMatchAndSwitches()
    {
        var match = FortniteMatchStats.Build(Data(), Me, null);
        var view = MatchStatsViewModel.FromFortnite(match, MatchClipMapping.ReplayWindow(match, ClipAt(110, 140, 30)), string.Empty);
        Assert.True(view.IsClipScope);
        Assert.Equal("IN THIS CLIP", view.TilesLabel);
        Assert.Equal(["1", "1", "—", "—"], view.Tiles.Select(tile => tile.Value));
        Assert.Equal(2, view.ClipEvents.Count);
        view.SelectedScope = MatchStatsViewModel.FullScope;
        Assert.True(view.IsFortniteFullScope);
        Assert.Equal(["#12", "2", "900", "38%"], view.Tiles.Select(tile => tile.Value));
        Assert.Equal(5, view.Timeline.Count);
    }

    [Fact]
    public void CaptureWindowFollowsTrimsAndSpeed()
    {
        var info = new ClipInfo("Fortnite", null, CaptureStartUtc: StartUtc, CaptureEndUtc: StartUtc.AddSeconds(60));
        var trimmed = ClipCaptureWindow.Rebase(info, 10, 40, 60);
        Assert.Equal(StartUtc.AddSeconds(10), trimmed.CaptureStartUtc);
        Assert.Equal(StartUtc.AddSeconds(40), trimmed.CaptureEndUtc);
        // A file already played at 2x: each media second is two of game.
        var fast = ClipCaptureWindow.Rebase(info, 5, 20, 30);
        Assert.Equal(StartUtc.AddSeconds(10), fast.CaptureStartUtc);
        Assert.Equal(StartUtc.AddSeconds(40), fast.CaptureEndUtc);
        var unknown = info with { CaptureStartUtc = null };
        Assert.Same(unknown, ClipCaptureWindow.Rebase(unknown, 1, 2, 3));
    }

    [Fact]
    public void CaptureDetailsKeepTheMatchSession()
    {
        using var folder = new TempFolder();
        var clip = System.IO.Path.Combine(folder.Path, "Clips", "Fortnite", "Fortnite - Oct-10-2026 - 19-30-00.mp4");
        ClipInfoSidecar.SaveCaptureDetails(folder.Path, clip, new ClipInfo("Fortnite", null, CaptureStartUtc: StartUtc, CaptureEndUtc: StartUtc.AddSeconds(30),
            MatchSessionId: "UnsavedReplay-2026.10.10-19.16.03.replay", MatchSessionStartUtc: StartUtc));
        ClipInfoSidecar.SaveCaptureDetails(folder.Path, clip, new ClipInfo("Fortnite", "Double Elimination"));
        var saved = ClipInfoSidecar.Load(folder.Path, clip)!;
        Assert.Equal("UnsavedReplay-2026.10.10-19.16.03.replay", saved.MatchSessionId);
        Assert.Equal(StartUtc, saved.CaptureStartUtc);
        Assert.Equal("Double Elimination", saved.AutoClipEventType);
    }

    // ---- Waiting, reading, ready ----

    [Fact]
    public void ALiveReplayRunsToNowWhenFindingAClipsReplay()
    {
        var live = new FortniteReplayHeader("a.replay", 1, StartUtc, StartUtc, 10, true, true, 0);
        var older = new FortniteReplayHeader("b.replay", 1, StartUtc, StartUtc.AddMinutes(-30), 600, false, false, 0);
        Assert.Same(live, FortniteReplayService.Best([older, live], StartUtc.AddMinutes(5), StartUtc.AddMinutes(5.5), StartUtc.AddMinutes(6)));
        Assert.Null(FortniteReplayService.Best([older], StartUtc.AddMinutes(5), StartUtc.AddMinutes(5.5), StartUtc.AddMinutes(6)));
    }

    [Fact]
    public void ClipGoesFromWaitingToReadingToReadyWhenTheMatchEnds()
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        var store = new MatchStatsStore(library.Path);
        var service = new FortniteReplayService(() => store, () => false, () => true, (_, _) => true, demos.Path, demos.Path);
        using var parsed = new ManualResetEventSlim();
        service.MatchParsed += (_, _) => parsed.Set();
        try
        {
            var localStart = DateTime.Now.AddMinutes(-5);
            var path = System.IO.Path.Combine(demos.Path, "UnsavedReplay-test.replay");
            File.WriteAllBytes(path, Replay(live: true, localStart, 0));
            service.Rescan();
            var startUtc = FortniteReplayFile.LocalToUtc(localStart);
            var clip = new ClipTimeline(startUtc.AddMinutes(2), startUtc.AddMinutes(2.5), 30, null, true, []);
            Assert.Equal(1, service.ReplayCount);

            // Mid-match: the replay is still being written.
            Assert.Equal(FortniteClipReplayState.Waiting, service.Resolve(clip).State);
            var waiting = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out _);
            Assert.Equal("Waiting for match to end", waiting.EmptyTitle);
            Assert.True(waiting.IsWaitingForMatch);
            Assert.False(waiting.HasMatch);

            // The match ends: Fortnite finishes the file. Not decoded yet.
            File.WriteAllBytes(path, Replay(live: false, localStart, 300_000, Match, (9, 0.38f, 3702), (3, 96)));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            service.Rescan();
            Assert.Equal(FortniteClipReplayState.Ready, service.Resolve(clip).State);
            var reading = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out _);
            Assert.Equal("Processing match stats", reading.EmptyTitle);
            Assert.True(reading.IsProcessingMatchStats);

            // The worker picks it up; the same clip now has its match.
            service.Start();
            Assert.True(parsed.Wait(TimeSpan.FromSeconds(10)));
            var ready = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out var match);
            Assert.True(ready.HasMatch);
            Assert.Equal(3, match!.Placement);
            Assert.Equal("#3", ready.ScoreLabel);
            // Nothing in the replay folder was written, moved or added.
            Assert.Equal([path], Directory.EnumerateFiles(demos.Path));
        }
        finally { service.Dispose(); }
    }

    [Theory]
    [InlineData("|parser-1")]
    [InlineData("|parser-2")]
    [InlineData("")]
    public void ParserUpgradeKeepsExistingStatsVisibleWhileGameDefersRefresh(string parserSuffix)
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        var store = new MatchStatsStore(library.Path);
        using var service = new FortniteReplayService(() => store, () => true, () => true, (_, _) => true, demos.Path, demos.Path);
        var localStart = DateTime.Now.AddMinutes(-5);
        var path = System.IO.Path.Combine(demos.Path, "UnsavedReplay-test.replay");
        File.WriteAllBytes(path, Replay(false, localStart, 300_000, Match));
        service.Rescan();
        var header = Assert.Single(service.Headers());
        var cached = FortniteMatchStats.Build(Data(), Me, null) with
        {
            StartedUtc = header.StartUtc,
            EndedUtc = header.EndUtc,
            SourceKey = $"{header.FileName}|{header.SizeBytes}|{header.LastWriteUtc.Ticks}{parserSuffix}"
        };
        store.Save(cached);
        var clip = new ClipTimeline(header.StartUtc.AddMinutes(2), header.StartUtc.AddMinutes(2.5), 30, null, true, []);
        var view = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out var match);
        Assert.True(view.HasMatch);
        Assert.False(view.IsProcessingMatchStats);
        Assert.Equal(cached.SourceKey, match!.SourceKey);
        // The parser cache stays strict so the queued worker still refreshes.
        Assert.Null(service.Cached(header));

        File.SetLastWriteTimeUtc(path, header.LastWriteUtc.AddSeconds(1));
        service.Rescan();
        var changed = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out var changedMatch);
        Assert.Null(changedMatch);
        Assert.True(changed.IsProcessingMatchStats);
    }

    [Fact]
    public void NoReplaysTellsYouToTurnOnRecordReplays()
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        var store = new MatchStatsStore(library.Path);
        using var service = new FortniteReplayService(() => store, () => false, () => true, (_, _) => true, demos.Path, demos.Path);
        service.Rescan();
        var clip = new ClipTimeline(StartUtc, StartUtc.AddSeconds(30), 30, null, true, []);
        var view = MainWindowViewModel.FortniteView(store, service, clip, enabled: true, out _);
        Assert.Contains("Record Replays", view.EmptyMessage);
        Assert.Contains("Settings › Game › Replays", view.EmptyMessage);
        Assert.Contains("off", MainWindowViewModel.FortniteView(store, service, clip, enabled: false, out _).EmptyMessage);
    }

    [Fact]
    public void AReplayIsNotDecodedWhenNothingInTheLibraryIsFromIt()
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        var store = new MatchStatsStore(library.Path);
        var service = new FortniteReplayService(() => store, () => false, () => true, (_, _) => false, demos.Path, demos.Path);
        File.WriteAllBytes(System.IO.Path.Combine(demos.Path, "UnsavedReplay-test.replay"), Replay(false, DateTime.Now.AddHours(-1), 300_000, Match));
        service.Rescan();
        service.Backfill();
        Assert.Empty(store.LoadAll());
        service.Dispose();
    }

    [Fact]
    public void FinishedReplayWaitsUntilGameStopsBeforeDecoding()
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        var store = new MatchStatsStore(library.Path);
        var running = 1;
        using var service = new FortniteReplayService(() => store, () => Volatile.Read(ref running) == 1,
            () => true, (_, _) => true, demos.Path, demos.Path);
        using var parsed = new ManualResetEventSlim();
        service.MatchParsed += (_, _) => parsed.Set();
        var path = System.IO.Path.Combine(demos.Path, "UnsavedReplay-deferred.replay");
        File.WriteAllBytes(path, Replay(false, DateTime.Now.AddMinutes(-5), 300_000, Match, (9, .38f, 3702), (3, 96)));
        service.Rescan();
        service.Backfill();
        service.Start();
        Assert.False(parsed.Wait(TimeSpan.FromMilliseconds(250)));
        Assert.Empty(store.LoadAll());
        Volatile.Write(ref running, 0);
        service.Rescan();
        Assert.True(parsed.Wait(TimeSpan.FromSeconds(10)));
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void MissingNamedReplayCannotResolveToAnotherOverlappingFile()
    {
        using var library = new TempFolder();
        using var demos = new TempFolder();
        using var service = new FortniteReplayService(() => new MatchStatsStore(library.Path), () => false, () => true,
            (_, _) => true, demos.Path, demos.Path);
        File.WriteAllBytes(System.IO.Path.Combine(demos.Path, "another.replay"), Replay(false, DateTime.Now.AddMinutes(-5), 300_000));
        service.Rescan();
        var header = Assert.Single(service.Headers());
        var clip = new ClipTimeline(header.StartUtc.AddSeconds(150), header.StartUtc.AddSeconds(180), 30, null, true, [],
            MatchSessionId: "original.replay");
        Assert.Equal(FortniteClipReplayState.None, service.Resolve(clip).State);
    }
}
