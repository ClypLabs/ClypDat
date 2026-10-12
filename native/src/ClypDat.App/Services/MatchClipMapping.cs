namespace ClypDat.App.Services;

/// <summary>
/// Where a clip or recording sits in wall-clock time, as far as its metadata
/// can say. StartUtc/EndUtc are estimates (a clip's file is created as it is
/// saved, so its end is the creation time); Source is the recorder's exact
/// timeline for this clip, valid only within the boot that wrote it.
/// </summary>
internal sealed record ClipTimeline(
    DateTime StartUtc,
    DateTime EndUtc,
    double DurationSeconds,
    SpotifySourceWindow? Source,
    bool TimestampReliable,
    IReadOnlyList<ClipEventMarker> ExistingMarkers,
    // The window came from the clip's recorded capture times (exact, and
    // kept through trims and exports), not from its file's creation time.
    bool Exact = false,
    string? MatchSessionId = null,
    IReadOnlyList<ClipCaptureSegment>? CaptureSegments = null,
    double? CaptureMediaDurationSeconds = null);

// Ties matches to clips. A match is found by overlap; its events are placed
// on the clip's timeline only when that placement can be trusted: exactly,
// through the recorder's clock in the boot the match was played in, or by
// lining the match's kills and deaths up with the clip's own auto-clip
// markers. Anything ambiguous gets no markers rather than wrong ones.
internal static class MatchClipMapping
{
    private const double AnchorTolerance = 1.0;
    private const double DuplicateWindow = 1.5;
    private static readonly HashSet<string> KillMarkerIds = new(StringComparer.OrdinalIgnoreCase) { "kill", "2k", "3k", "4k", "ace", "headshot" };

    public static bool IsCs2(string? gameName, string? autoClipGameId) =>
        string.Equals(autoClipGameId, "cs2", StringComparison.OrdinalIgnoreCase) ||
        gameName is not null && (gameName.Contains("Counter-Strike", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(gameName.Trim(), "CS2", StringComparison.OrdinalIgnoreCase));

    public static ClipTimeline Describe(ClipInfo? info, DateTimeOffset fileCreated, TimeSpan duration, bool isRecording, SpotifySourceWindow? source)
    {
        if (info is { IsTrimmed: false, IsExport: false } && source is not null && info.CaptureStartUtc is null)
            info = ClipCaptureWindow.FromSource(info, source);
        if (info is { CaptureStartUtc: { } captureStart, CaptureEndUtc: { } captureEnd } && captureEnd > captureStart)
            return new ClipTimeline(captureStart, captureEnd, duration.TotalSeconds, source, true,
                info.AutoClipMarkers ?? Array.Empty<ClipEventMarker>(), Exact: info.CaptureTimingVerified, info.MatchSessionId,
                info.CaptureSegments, info.CaptureMediaDurationSeconds);
        var anchor = (info?.CapturedAt ?? fileCreated).UtcDateTime;
        // A recording's file is created when it starts; a clip's when it is
        // saved, which is where it ends.
        var start = isRecording ? anchor : anchor - duration;
        var reliable = info is not null && !info.IsTrimmed && !info.IsExport &&
                       string.IsNullOrWhiteSpace(info.MedalImportKey) && string.IsNullOrWhiteSpace(info.SteelSeriesImportKey);
        return new ClipTimeline(start, start + duration, duration.TotalSeconds, source, reliable,
            info?.AutoClipMarkers ?? Array.Empty<ClipEventMarker>());
    }

    // How far a match's own times can be off: GSI is this PC's clock; a
    // demo-only match is placed from Valve's match time, which is not.
    // A Fortnite replay's header is this PC's clock too.
    private static TimeSpan Slack(MatchStats match) =>
        match.HasLiveData ? TimeSpan.FromMinutes(2) : match.Game == FortniteMatchStats.Game ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10);

    public static MatchStats? FindMatch(IEnumerable<MatchStats> matches, ClipTimeline clip, string game = "cs2")
    {
        if (game == FortniteMatchStats.Game && clip.MatchSessionId is { } session)
            return matches.FirstOrDefault(match => match.Game == game && match.Id == FortniteMatchStats.IdFor(session));
        MatchStats? best = null;
        var bestOverlap = TimeSpan.Zero;
        foreach (var match in matches)
        {
            if (match.StartedUtc == default || match.EndedUtc < match.StartedUtc || !string.Equals(match.Game, game, StringComparison.OrdinalIgnoreCase)) continue;
            // The replay noted when the clip was saved wins outright.
            var slack = Slack(match);
            var overlap = Min(match.EndedUtc + slack, clip.EndUtc) - Max(match.StartedUtc - slack, clip.StartUtc);
            if (overlap <= TimeSpan.Zero || overlap <= bestOverlap) continue;
            best = match;
            bestOverlap = overlap;
        }
        return best;
    }

    /// <summary>
    /// Where a clip sits in a replay-timed match (Fortnite): clip start minus
    /// replay start, both this PC's clock. Null when the clip's time cannot be
    /// trusted (a trimmed, exported or imported clip with no recorded capture
    /// window) or it does not overlap the replay.
    /// </summary>
    public static MatchReplayWindow? ReplayWindow(MatchStats match, ClipTimeline clip)
    {
        if (!clip.Exact || match.StartedUtc == default || clip.DurationSeconds <= 0) return null;
        var start = (clip.StartUtc - match.StartedUtc).TotalSeconds;
        var end = (clip.EndUtc - match.StartedUtc).TotalSeconds;
        var length = match.DemoDurationSeconds ?? (match.EndedUtc - match.StartedUtc).TotalSeconds;
        if (end <= start || end <= 0 || start >= length) return null;
        var window = new MatchReplayWindow(start, end, (end - start) / clip.DurationSeconds);
        if (clip.CaptureSegments is not { Count: > 0 } segments) return window;
        if (segments.Count > 256 || clip.CaptureMediaDurationSeconds is not > 0 ||
            !double.IsFinite(clip.CaptureMediaDurationSeconds.Value)) return null;
        var mediaScale = clip.DurationSeconds / clip.CaptureMediaDurationSeconds.Value;
        var mapped = new List<MatchReplaySegment>();
        foreach (var segment in segments)
        {
            if (segment.EndUtc <= segment.StartUtc || !double.IsFinite(segment.MediaStartSeconds) ||
                !double.IsFinite(segment.MediaEndSeconds) || segment.MediaStartSeconds < 0 ||
                segment.MediaEndSeconds <= segment.MediaStartSeconds || segment.MediaEndSeconds > clip.CaptureMediaDurationSeconds.Value + 0.001) return null;
            mapped.Add(new((segment.StartUtc - match.StartedUtc).TotalSeconds, (segment.EndUtc - match.StartedUtc).TotalSeconds,
                segment.MediaStartSeconds * mediaScale, segment.MediaEndSeconds * mediaScale));
        }
        return window with { Segments = mapped };
    }

    public static IReadOnlyList<ClipEventMarker> Markers(MatchStats match, ClipTimeline clip)
    {
        if (match.Game == FortniteMatchStats.Game) return FortniteMarkers(match, clip);
        var placed = PlaceExactly(match, clip) ?? PlaceByAnchors(match, clip);
        if (placed is null) return [];
        return placed
            .Where(item => double.IsFinite(item.Offset) && item.Offset >= 0 && item.Offset <= clip.DurationSeconds)
            .Select(item => ToMarker(item.Event, item.Offset))
            .OfType<ClipEventMarker>()
            .Where(marker => !DuplicatesExisting(marker, clip.ExistingMarkers))
            .OrderBy(marker => marker.OffsetSeconds)
            .ToArray();
    }

    private static List<(MatchEvent Event, double Offset)>? PlaceExactly(MatchStats match, ClipTimeline clip)
    {
        if (clip.Source is not { } source || match.BootId is null || source.BootId != match.BootId) return null;
        var placed = match.Events.Where(e => e.SharedSeconds is not null)
            .Select(e => (e, source.VideoMediaSeconds(e.SharedSeconds!.Value))).ToList();
        return placed.Count > 0 ? placed : null;
    }

    // Lines the match up with the clip's own kill/death markers. Needs at
    // least one anchor for a GSI match (its clock is this PC's) and two for a
    // demo-only one, whose start time is only known to within minutes; and
    // exactly one way of lining them up.
    private static List<(MatchEvent Event, double Offset)>? PlaceByAnchors(MatchStats match, ClipTimeline clip)
    {
        if (!clip.TimestampReliable) return null;
        var anchors = clip.ExistingMarkers
            .Select(marker => (Kind: AnchorKind(marker.EventId), marker.OffsetSeconds))
            .Where(anchor => anchor.Kind is not null)
            .OrderBy(anchor => anchor.OffsetSeconds).ToList();
        // A kill's auto-clip marker and its headshot marker share one moment.
        anchors = anchors.Where((anchor, index) => index == 0 || anchor.Kind != anchors[index - 1].Kind ||
                                                   anchor.OffsetSeconds - anchors[index - 1].OffsetSeconds > 0.3).ToList();
        var minimumAnchors = match.HasLiveData ? 1 : 2;
        if (anchors.Count < minimumAnchors) return null;

        var timed = match.Events.Select(e => (Event: e, Wall: WallTime(match, e)))
            .Where(item => item.Wall is not null)
            .Select(item => (item.Event, Estimated: (item.Wall!.Value - clip.StartUtc).TotalSeconds)).ToList();
        var tolerance = Slack(match).TotalSeconds;
        var first = anchors[0];
        var shifts = new List<double>();
        foreach (var candidate in timed.Where(item => item.Event.Kind == first.Kind && Math.Abs(item.Estimated - first.OffsetSeconds) <= tolerance))
        {
            var shift = first.OffsetSeconds - candidate.Estimated;
            var all = anchors.All(anchor => timed.Any(item => item.Event.Kind == anchor.Kind &&
                                                              Math.Abs(item.Estimated + shift - anchor.OffsetSeconds) <= AnchorTolerance));
            if (all) shifts.Add(shift);
        }
        if (shifts.Count == 0) return null;
        // Several candidate kills can line up to (nearly) the same shift.
        if (shifts.Max() - shifts.Min() > AnchorTolerance) return null;
        var chosen = shifts.Average();
        return timed.Select(item => (item.Event, item.Estimated + chosen)).ToList();
    }

    private static IReadOnlyList<ClipEventMarker> FortniteMarkers(MatchStats match, ClipTimeline clip)
    {
        if (ReplayWindow(match, clip) is not { } window) return [];
        return match.Events
            .Where(e => e.DemoSeconds is { } seconds && window.Contains(seconds))
            .Select(e => FortniteMarker(e, window.ToClipSeconds(e.DemoSeconds!.Value)))
            .OfType<ClipEventMarker>()
            .Where(marker => marker.OffsetSeconds >= 0 && marker.OffsetSeconds <= clip.DurationSeconds)
            .OrderBy(marker => marker.OffsetSeconds)
            .ToArray();
    }

    internal static ClipEventMarker? FortniteMarker(MatchEvent e, double offset) => e.Kind switch
    {
        "kill" => new("fortnite-elimination", "Elimination", offset),
        "knock" => new("fortnite-knock", "Knock", offset),
        "knocked" => new("fortnite-knocked", "Knocked down", offset),
        "death" => new("death", "Eliminated", offset),
        "victory" => new("victory", "Victory Royale", offset),
        _ => null
    };

    internal static DateTime? WallTime(MatchStats match, MatchEvent e) =>
        e.OccurredUtc ?? (e.DemoSeconds is { } seconds && match.StartedUtc != default ? match.StartedUtc.AddSeconds(seconds) : null);

    private static string? AnchorKind(string eventId) =>
        KillMarkerIds.Contains(eventId) ? "kill" : string.Equals(eventId, "death", StringComparison.OrdinalIgnoreCase) ? "death" : null;

    internal static ClipEventMarker? ToMarker(MatchEvent e, double offset) => e.Kind switch
    {
        "kill" => e.Headshot ? new("headshot", "Headshot kill", offset) : new("kill", "Kill", offset),
        "death" => new("death", "Death", offset),
        "assist" => new("assist", "Assist", offset),
        "bomb_planted" => new("bomb_plant", "Bomb planted", offset),
        "bomb_defused" => new("bomb_defuse", "Bomb defused", offset),
        "bomb_exploded" => new("objective_bomb_exploded", "Bomb exploded", offset),
        "round_end" => e.Won switch
        {
            true => new("round_win", $"Round {e.Round} won", offset),
            false => new("round_lost", $"Round {e.Round} lost", offset),
            _ => new("round_end", $"Round {e.Round} ended", offset)
        },
        _ => null
    };

    private static bool DuplicatesExisting(ClipEventMarker marker, IReadOnlyList<ClipEventMarker> existing)
    {
        var family = Family(marker.EventId);
        return existing.Any(item => Family(item.EventId) == family && Math.Abs(item.OffsetSeconds - marker.OffsetSeconds) <= DuplicateWindow);
    }

    private static string Family(string id) => KillMarkerIds.Contains(id) ? "kill" : id.ToLowerInvariant();

    /// <summary>
    /// Whether a parsed demo is the match GSI recorded: same map, Valve's
    /// match time near it, and the round results agreeing where both have them.
    /// </summary>
    public static bool IsSameMatch(MatchStats live, MatchStats demo, DateTimeOffset? matchTime)
    {
        if (live.Map is { } liveMap && demo.Map is { } demoMap && !string.Equals(liveMap, demoMap, StringComparison.OrdinalIgnoreCase)) return false;
        if (matchTime is not { } time) return false;
        var utc = time.UtcDateTime;
        if (utc < live.StartedUtc - TimeSpan.FromHours(3) || utc > live.EndedUtc + TimeSpan.FromHours(3)) return false;
        var common = live.Rounds.Join(demo.Rounds, round => round.Number, round => round.Number, (a, b) => (a.WinnerSide, b.WinnerSide))
            .Where(pair => pair.Item1 is not null && pair.Item2 is not null).ToList();
        if (common.Count == 0 || Math.Abs(live.Rounds.Count - demo.Rounds.Count) > 2) return false;
        return common.Count(pair => pair.Item1 == pair.Item2) >= Math.Ceiling(common.Count * 0.8);
    }

    /// <summary>Demo numbers on the GSI match's times.</summary>
    public static MatchStats Merge(MatchStats live, MatchStats demo)
    {
        var offsets = demo.Rounds.Join(live.Rounds, round => round.Number, round => round.Number, (d, l) => (d.DemoSeconds, l.EndedUtc))
            .Where(pair => pair.DemoSeconds is not null && pair.EndedUtc is not null)
            .Select(pair => (pair.EndedUtc!.Value - DateTime.UnixEpoch).TotalSeconds - pair.DemoSeconds!.Value)
            .OrderBy(value => value).ToList();
        double? offset = offsets.Count > 0 ? offsets[offsets.Count / 2] : null;
        DateTime? ToWall(double? seconds) => seconds is { } value && offset is { } shift ? DateTime.UnixEpoch.AddSeconds(value + shift) : null;

        var localId = live.LocalSteamId ?? demo.LocalSteamId;
        var players = demo.Players.Select(player => player with { IsLocal = localId is not null && player.SteamId == localId }).ToArray();
        var local = players.FirstOrDefault(player => player.IsLocal);
        var rounds = demo.Rounds.Select(round => round with
        {
            EndedUtc = live.Rounds.FirstOrDefault(item => item.Number == round.Number)?.EndedUtc ?? ToWall(round.DemoSeconds)
        }).ToArray();
        var events = live.Events.Count > 0
            ? live.Events
            : demo.Events.Select(e => e with { OccurredUtc = ToWall(e.DemoSeconds) }).ToArray();
        return demo with
        {
            Id = live.Id,
            Mode = live.Mode ?? demo.Mode,
            Map = live.Map ?? demo.Map,
            StartedUtc = live.StartedUtc,
            EndedUtc = live.EndedUtc,
            BootId = live.BootId,
            HasLiveData = true,
            LocalPlayerName = local?.Name ?? live.LocalPlayerName,
            LocalSteamId = localId,
            LocalTeamId = local?.TeamId ?? live.LocalTeamId,
            HalftimeAfterRound = demo.HalftimeAfterRound > 0 ? demo.HalftimeAfterRound : live.HalftimeAfterRound,
            Players = players,
            Rounds = rounds,
            Events = events
        };
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
