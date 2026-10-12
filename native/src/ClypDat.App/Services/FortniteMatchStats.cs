namespace ClypDat.App.Services;

// Turns a decoded Fortnite replay into the game-agnostic MatchStats. Pure,
// so the maths can be tested without a replay file.
internal static class FortniteMatchStats
{
    public const string Game = "fortnite";

    public static string IdFor(string replayFileName) => "fortnite-" + Path.GetFileNameWithoutExtension(replayFileName);

    public static bool IsFortnite(string? gameName, string? autoClipGameId) =>
        string.Equals(autoClipGameId, Game, StringComparison.OrdinalIgnoreCase) ||
        gameName is not null && gameName.Contains("Fortnite", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which local account recorded the replay: the one that appears in its
    /// eliminations, preferring the one whose last elimination is when the
    /// replay's stats were written (that is when the recording player's match
    /// ended). With a single local account that never fought, still that one.
    /// </summary>
    public static string? ResolveOwner(FortniteReplayData data, IReadOnlyList<string> accountIds)
    {
        if (accountIds.Count == 0) return null;
        var present = accountIds.Where(id => data.Eliminations.Any(e => Same(e.Eliminator, id) || Same(e.Eliminated, id))).ToList();
        if (present.Count == 1) return present[0];
        if (present.Count == 0) return accountIds.Count == 1 ? accountIds[0] : null;
        if (data.Stats is not { } stats) return null;
        var byEnd = present
            .Select(id => (Id: id, Gap: data.Eliminations.Where(e => Same(e.Eliminated, id) && !e.Knocked)
                .Select(e => Math.Abs(e.Seconds - stats.Seconds)).DefaultIfEmpty(double.MaxValue).Min()))
            .OrderBy(item => item.Gap).ToList();
        return byEnd[0].Gap <= 2 && byEnd[0].Gap < byEnd[1].Gap ? byEnd[0].Id : null;
    }

    public static MatchStats Build(FortniteReplayData data, string? ownerId, string? ownerName)
    {
        var header = data.Header;
        var stats = data.Stats;
        var mine = ownerId is null ? [] : data.Eliminations.Where(e => Same(e.Eliminator, ownerId) && !Same(e.Eliminated, ownerId)).ToList();
        var against = ownerId is null ? [] : data.Eliminations.Where(e => Same(e.Eliminated, ownerId)).ToList();
        var deaths = against.Where(e => !e.Knocked).ToList();
        var won = data.Placement == 1;
        var finalDeath = won ? null : deaths.LastOrDefault();
        double? end = finalDeath?.Seconds ?? stats?.Seconds ?? (won ? data.Eliminations.LastOrDefault()?.Seconds : null);

        var isBattleRoyale = data.TotalPlayers is > 2 && data.Placement is not null;
        var steps = isBattleRoyale && data.PlayersLeftComplete ? data.PlayersLeft : [];
        int? leftAtEnd = steps.Count > 0 && end is { } endSeconds && steps[0].Seconds <= endSeconds
            ? PlayersLeftBefore(steps, steps[0].Count, endSeconds, inclusive: won) : null;

        var events = new List<MatchEvent>();
        DateTime At(double seconds) => header.StartUtc.AddSeconds(seconds);
        foreach (var e in mine)
            events.Add(new MatchEvent { Kind = e.Knocked ? "knock" : "kill", DemoSeconds = e.Seconds, OccurredUtc = At(e.Seconds), Bot = e.EliminatedIsBot });
        foreach (var e in against)
            events.Add(new MatchEvent
            {
                Kind = e.Knocked ? "knocked" : "death",
                DemoSeconds = e.Seconds,
                OccurredUtc = At(e.Seconds),
                Final = ReferenceEquals(e, finalDeath)
            });
        if (won && end is { } victory)
            events.Add(new MatchEvent { Kind = "victory", DemoSeconds = victory, OccurredUtc = At(victory), Won = true, Final = true });
        events.Sort((a, b) => (a.DemoSeconds ?? 0).CompareTo(b.DemoSeconds ?? 0));

        var player = new MatchPlayer
        {
            Name = string.IsNullOrWhiteSpace(ownerName) ? "You" : ownerName,
            IsLocal = true,
            TeamId = "local",
            Kills = stats?.Eliminations ?? mine.Count(e => !e.Knocked),
            Deaths = deaths.Count,
            Assists = stats?.Assists ?? 0,
            Damage = stats?.DamageToPlayers,
            Knocks = ownerId is null ? null : mine.Count(e => e.Knocked),
            AccuracyPercent = stats is null ? null : Math.Clamp(stats.Accuracy * 100, 0, 100),
            DamageTaken = stats?.DamageTaken,
            StructureDamage = stats?.DamageToStructures,
            MaterialsGathered = stats?.MaterialsGathered,
            MaterialsUsed = stats?.MaterialsUsed,
            LootingPickups = data.LootingPickups,
            LootingPartial = data.LootingPartial,
            DistanceMetres = stats is null ? null : stats.TravelledCentimetres / 100,
            Revives = stats?.Revives
        };

        return new MatchStats
        {
            Id = IdFor(header.FileName),
            Game = Game,
            Mode = ModeLabel(data.Playlist),
            Playlist = data.Playlist,
            ZeroBuild = IsZeroBuild(data.Playlist),
            StartedUtc = header.StartUtc,
            EndedUtc = header.EndUtc,
            HasDemoData = true,
            DemoStatus = "parsed",
            DemoDurationSeconds = header.LengthSeconds,
            LocalPlayerName = player.Name,
            OutfitId = data.OutfitId,
            LocalTeamId = "local",
            Players = [player],
            Events = events,
            Placement = data.Placement,
            PlayerCount = data.TotalPlayers,
            Won = won,
            LocalEndSeconds = end,
            PlayersLeftAtEnd = leftAtEnd,
            PlayersLeft = steps,
            SourceKey = header.CacheKey,
            HasMatchSummary = stats is not null,
            LocalEventsAvailable = ownerId is not null && data.SkippedEvents == 0,
            ReplayDetailsIncomplete = data.ReplayDetailsIncomplete
        };
    }

    // How many were left just as the player went out (they count), or, for
    // a win, once the last opponent did.
    internal static int PlayersLeftBefore(IReadOnlyList<MatchCountStep> steps, int total, double seconds, bool inclusive)
    {
        var left = total;
        foreach (var step in steps)
        {
            if (step.Seconds > seconds || !inclusive && step.Seconds >= seconds && step.Seconds > 0) break;
            left = step.Count;
        }
        return Math.Max(1, left);
    }

    /// <summary>Explicit NoBuild/ZeroBuild playlists, or known default BR build playlists; otherwise unknown.</summary>
    public static bool? IsZeroBuild(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)) return null;
        if (playlist.Contains("NoBuild", StringComparison.OrdinalIgnoreCase) || playlist.Contains("ZeroBuild", StringComparison.OrdinalIgnoreCase)) return true;
        return playlist.StartsWith("Playlist_Default", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>"Zero Build · Trios", "Battle Royale · Squads"; "Fortnite" when the playlist says nothing usable.</summary>
    public static string ModeLabel(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)) return "Fortnite";
        string? Has(string token, string label) => playlist.Contains(token, StringComparison.OrdinalIgnoreCase) ? label : null;
        var team = Has("Solo", "Solo") ?? Has("Duo", "Duos") ?? Has("Trio", "Trios") ?? Has("Squad", "Squads");
        var family = IsZeroBuild(playlist) == true ? "Zero Build"
            : playlist.Contains("BR", StringComparison.Ordinal) || playlist.Contains("Default", StringComparison.OrdinalIgnoreCase) ? "Battle Royale"
            : null;
        var parts = new[] { family, team }.OfType<string>().ToArray();
        return parts.Length == 0 ? "Fortnite" : string.Join(" · ", parts);
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
