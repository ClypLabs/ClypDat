using System.Text.Json;

namespace ClypDat.App.Services;

// The game a clip was captured from and, for a CS2 auto-clip, which event
// triggered it (e.g. "3K", "Ace", "Headshot", "Death") - written once at save
// time so the library can show the actual game name and a per-event icon on
// the tile instead of parsing it back out of the clip's filename, which for a
// manual clip is just the game name and for an auto-clip is "<event> - <map>".
// CustomTitle is a separate, user-set display label shown in place of "Clip
// from {date}" for non-auto-clip cards (manual clips, VODs, external imports) -
// deliberately independent of FileTitle/GameDisplayName so renaming a clip
// never touches the game association or, for a Medal import, its original
// event title (e.g. "4K - Inferno").
public sealed record ClipEventMarker(string EventId, string EventLabel, double OffsetSeconds);

// A saved video can omit pauses or dropped frames between retained intervals.
public sealed record ClipCaptureSegment(DateTime StartUtc, DateTime EndUtc, double MediaStartSeconds, double MediaEndSeconds);

public static class ClipEventMarkerMapping
{
    internal static IReadOnlyList<ClipEventMarker> FromSavedWindow(IEnumerable<AutoClipEvent> events,
        DateTime requestedStartUtc, DateTime requestedEndUtc, SpotifySourceWindow? savedSource)
    {
        if (savedSource is null) return FromEvents(events, requestedStartUtc, requestedEndUtc);
        // The ring may contain less than its configured history after startup
        // or recovery. The saved source window records what was actually kept.
        return events.Select(item => new ClipEventMarker(item.Id, item.Label,
                savedSource.VideoMediaSeconds(MonotonicClock.ToSharedSeconds(item.OccurredUtc))))
            .Where(item => double.IsFinite(item.OffsetSeconds) && item.OffsetSeconds >= 0
                && item.OffsetSeconds <= savedSource.DurationSeconds)
            .ToArray();
    }

    public static IReadOnlyList<ClipEventMarker> FromEvents(IEnumerable<AutoClipEvent> events, DateTime clipStartUtc, DateTime clipEndUtc)
    {
        return events
            .Select(item => new ClipEventMarker(item.Id, item.Label, (item.OccurredUtc - clipStartUtc).TotalSeconds))
            .Where(item => double.IsFinite(item.OffsetSeconds)
                && item.OffsetSeconds >= 0
                && item.OffsetSeconds <= (clipEndUtc - clipStartUtc).TotalSeconds)
            .ToArray();
    }
}

public sealed record ClipInfo(
    string? GameDisplayName,
    string? AutoClipEventType,
    string? FileTitle = null,
    DateTimeOffset? CapturedAt = null,
    string? MedalImportKey = null,
    string? CustomTitle = null,
    // Distinguishes a re-encoded Export from the original recording - both are
    // ordinary "manual" cards (no AutoClipEventType/import provenance) with the
    // same CustomTitle-or-fallback tile logic, but an untitled export should
    // read "Exported clip from" (see ClipCardViewModel.ClipFromLabel), not
    // "Clip from", since it's a derived copy rather than the actual recording.
    bool IsExport = false,
    // Set once Save Trim has replaced a clip with a shorter range of itself.
    bool IsTrimmed = false,
    // Null is legacy game capture. New desktop clips use "Desktop" so editor
    // behavior does not depend on transient capture sidecars.
    string? CaptureSource = null,
    string? SteelSeriesImportKey = null,
    string? AutoClipProviderId = null,
    string? AutoClipGameId = null,
    IReadOnlyList<string>? AutoClipEventIds = null,
    string? AutoClipPackId = null,
    string? AutoClipPackVersion = null,
    string? AutoClipPackHash = null,
    Guid? AutoClipPlanId = null,
    IReadOnlyList<ClipEventMarker>? AutoClipMarkers = null,
    // What Spotify was playing when the clip was saved, read from the last poll
    // rather than fetched at save time - a save is already the most latency
    // sensitive moment in the app. Null for every clip captured without the
    // connection, which is what tells the overlay it has nothing to draw.
    string? SpotifyTrack = null,
    string? SpotifyArtist = null,
    string? SpotifyAlbum = null,
    int? SpotifyDurationMs = null,
    // Where the track had reached when the clip was saved, so the card's
    // progress bar says something true rather than sitting at zero.
    int? SpotifyProgressMs = null,
    // The cover art, already downloaded and stored beside the library's other
    // sidecars. A path rather than the URL: the card has to render for a clip
    // exported months later, offline, after the URL has expired.
    string? SpotifyArtPath = null,
    // Burn state is output provenance, never inferred from current settings.
    // A permanently drawn card must not be drawn again by editor/export.
    bool SpotifyOverlayBurned = false,
    ClipOverlayManifest? OverlayManifest = null,
    // The clip's most recent link from Get link (clypdat.xyz/c/<id>), so the
    // library tile copies it instead of uploading the clip again.
    string? SharedClipId = null,
    string? SharedClipUrl = null,
    // The clip's real span in wall-clock time, read from the recorder's own
    // clock at save time and moved with every trim or export. Media seconds
    // map onto it linearly, so a sped-up export still lines up. Lets match
    // stats find the clip's exact window inside a game's own replay.
    DateTime? CaptureStartUtc = null,
    DateTime? CaptureEndUtc = null,
    // The game recording the clip was saved during (Fortnite: the replay's
    // file name) and when that recording started, noted at save time.
    string? MatchSessionId = null,
    DateTime? MatchSessionStartUtc = null,
    IReadOnlyList<ClipCaptureSegment>? CaptureSegments = null,
    double? CaptureMediaDurationSeconds = null,
    bool CaptureTimingVerified = false);

public static class ClipCaptureWindow
{
    internal static ClipInfo FromSource(ClipInfo info, SpotifySourceWindow source)
    {
        if (source.BootId != MonotonicClock.BootId || !double.IsFinite(source.DurationSeconds) || source.DurationSeconds <= 0) return info;
        // The audio map deliberately retimes sound to fit the muxed video;
        // it is not evidence of which game frames the video actually kept.
        if (source.VideoMappings is not { Count: > 0 } mappings || mappings.Count > 100_000) return info;
        var segments = new List<ClipCaptureSegment>();
        double previousSource = double.NegativeInfinity;
        try
        {
            foreach (var mapping in mappings.OrderBy(item => item.OutputStartSeconds))
            {
                if (!double.IsFinite(mapping.SourceStartSeconds) || !double.IsFinite(mapping.DurationSeconds) ||
                    !double.IsFinite(mapping.OutputStartSeconds) || mapping.DurationSeconds <= 0 || mapping.OutputStartSeconds < 0) return info;
                var mediaEnd = Math.Min(source.DurationSeconds, mapping.OutputStartSeconds + mapping.DurationSeconds);
                if (mediaEnd <= mapping.OutputStartSeconds) continue;
                var start = MonotonicClock.FromSharedSeconds(mapping.SourceStartSeconds);
                var end = MonotonicClock.FromSharedSeconds(mapping.SourceStartSeconds + mediaEnd - mapping.OutputStartSeconds);
                // Repeated/older source frames do not represent new game time.
                if (mapping.SourceStartSeconds <= previousSource) continue;
                previousSource = mapping.SourceStartSeconds;
                if (segments.LastOrDefault() is { } previous)
                {
                    if (mapping.OutputStartSeconds < previous.MediaEndSeconds - 0.000001) return info;
                    // Join cadence jitter only while every frame remains within
                    // 25 ms of the run's original clock anchor. Drift cannot
                    // accumulate merely because neighbours look contiguous.
                    if (Math.Abs(mapping.OutputStartSeconds - previous.MediaEndSeconds) < 0.000001 &&
                        Math.Abs((start - previous.StartUtc).TotalSeconds - (mapping.OutputStartSeconds - previous.MediaStartSeconds)) <= 0.025)
                    {
                        segments[^1] = previous with { EndUtc = previous.StartUtc.AddSeconds(mediaEnd - previous.MediaStartSeconds), MediaEndSeconds = mediaEnd };
                        continue;
                    }
                    if (start < previous.EndUtc) segments[^1] = previous with { EndUtc = start };
                }
                segments.Add(new(start, end, mapping.OutputStartSeconds, mediaEnd));
                if (segments.Count > 256) return info;
            }
        }
        catch (ArgumentOutOfRangeException) { return info; }
        return segments.Count == 0 ? info : info with
        {
            CaptureStartUtc = segments[0].StartUtc, CaptureEndUtc = segments[^1].EndUtc,
            CaptureSegments = segments, CaptureMediaDurationSeconds = source.DurationSeconds, CaptureTimingVerified = true
        };
    }

    /// <summary>
    /// The capture window of a clip cut from <paramref name="info"/>'s clip:
    /// media seconds <paramref name="fromSeconds"/>-<paramref name="toSeconds"/>
    /// of a source <paramref name="sourceDurationSeconds"/> long. Unchanged
    /// when the source has no window.
    /// </summary>
    public static ClipInfo Rebase(ClipInfo info, double fromSeconds, double toSeconds, double sourceDurationSeconds)
    {
        if (info.CaptureStartUtc is not { } start || info.CaptureEndUtc is not { } end || end <= start ||
            !double.IsFinite(sourceDurationSeconds) || sourceDurationSeconds <= 0 ||
            !double.IsFinite(fromSeconds) || !double.IsFinite(toSeconds) || toSeconds <= fromSeconds)
            return info;
        fromSeconds = Math.Clamp(fromSeconds, 0, sourceDurationSeconds);
        toSeconds = Math.Clamp(toSeconds, fromSeconds, sourceDurationSeconds);
        if (toSeconds <= fromSeconds) return info;
        if (info.CaptureSegments is { Count: > 0 } segments && info.CaptureMediaDurationSeconds is > 0 and { } reference && double.IsFinite(reference))
        {
            var mediaScale = sourceDurationSeconds / reference;
            var kept = new List<ClipCaptureSegment>();
            foreach (var segment in segments)
            {
                var mediaStart = segment.MediaStartSeconds * mediaScale;
                var mediaEnd = segment.MediaEndSeconds * mediaScale;
                if (!double.IsFinite(mediaStart) || !double.IsFinite(mediaEnd) || mediaEnd <= mediaStart || segment.EndUtc <= segment.StartUtc) return info;
                var cutStart = Math.Max(mediaStart, fromSeconds);
                var cutEnd = Math.Min(mediaEnd, toSeconds);
                if (cutEnd <= cutStart) continue;
                var segmentRate = (segment.EndUtc - segment.StartUtc).TotalSeconds / (mediaEnd - mediaStart);
                kept.Add(new(segment.StartUtc.AddSeconds((cutStart - mediaStart) * segmentRate),
                    segment.StartUtc.AddSeconds((cutEnd - mediaStart) * segmentRate), cutStart - fromSeconds, cutEnd - fromSeconds));
            }
            return kept.Count == 0 ? info with { CaptureStartUtc = null, CaptureEndUtc = null, CaptureSegments = null, CaptureMediaDurationSeconds = null, CaptureTimingVerified = false }
                : info with { CaptureStartUtc = kept[0].StartUtc, CaptureEndUtc = kept[^1].EndUtc, CaptureSegments = kept,
                    CaptureMediaDurationSeconds = toSeconds - fromSeconds };
        }
        var rate = (end - start).TotalSeconds / sourceDurationSeconds;
        return info with { CaptureStartUtc = start.AddSeconds(fromSeconds * rate), CaptureEndUtc = start.AddSeconds(toSeconds * rate) };
    }
}

public static class ClipInfoSidecar
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string SidecarPath(string libraryRoot, string clipPath)
    {
        return LibraryLayout.SidecarPath(libraryRoot, clipPath, ".info.json");
    }

    public static void Save(string libraryRoot, string clipPath, ClipInfo info)
    {
        try
        {
            var sidecarPath = SidecarPath(libraryRoot, clipPath);
            if (!LibraryPathGuard.IsWithin(libraryRoot, sidecarPath)) throw new InvalidDataException("Clip metadata crosses a filesystem link.");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(info, SerializerOptions));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar save failed: {clipPath}", error);
        }
    }

    public static void SaveCaptureDetails(string libraryRoot, string clipPath, ClipInfo details)
    {
        // The recorder has already saved its asset mappings before returning
        // the clip path. UI completion owns the event labels, not provenance.
        // Load here, after encoding, rather than snapshotting before the save.
        var existing = Load(libraryRoot, clipPath);
        Save(libraryRoot, clipPath, existing is null ? details : existing with
        {
            GameDisplayName = details.GameDisplayName,
            AutoClipEventType = details.AutoClipEventType,
            FileTitle = details.FileTitle,
            CapturedAt = existing.CapturedAt ?? details.CapturedAt,
            CaptureSource = details.CaptureSource,
            AutoClipMarkers = details.AutoClipMarkers,
            CaptureStartUtc = details.CaptureStartUtc ?? existing.CaptureStartUtc,
            CaptureEndUtc = details.CaptureEndUtc ?? existing.CaptureEndUtc,
            MatchSessionId = details.MatchSessionId ?? existing.MatchSessionId,
            MatchSessionStartUtc = details.MatchSessionStartUtc ?? existing.MatchSessionStartUtc,
            CaptureSegments = details.CaptureSegments ?? existing.CaptureSegments,
            CaptureMediaDurationSeconds = details.CaptureMediaDurationSeconds ?? existing.CaptureMediaDurationSeconds,
            CaptureTimingVerified = details.CaptureStartUtc is not null || details.CaptureSegments is not null
                ? details.CaptureTimingVerified : existing.CaptureTimingVerified
        });
    }

    /// <summary>Records (or, with nulls, forgets) the clip's shared link, keeping everything else.</summary>
    public static ClipInfo SaveSharedLink(string libraryRoot, string clipPath, string? clipId, string? url)
    {
        var info = (Load(libraryRoot, clipPath) ?? new ClipInfo(null, null)) with { SharedClipId = clipId, SharedClipUrl = url };
        Save(libraryRoot, clipPath, info);
        return info;
    }

    public static ClipInfo? Load(string libraryRoot, string clipPath)
    {
        string? path = null;
        try
        {
            path = SidecarPath(libraryRoot, clipPath);
            if (!LibraryPathGuard.IsWithin(libraryRoot, path)) return null;
            if (!File.Exists(path)) path = LibraryLayout.LegacySidecarPath(clipPath, ".info.json");
            if (!LibraryPathGuard.IsWithin(libraryRoot, path) || !File.Exists(path)) return null;
            var json = ReadBounded(path);
            return json is null ? null : JsonSerializer.Deserialize<ClipInfo>(json);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar read failed: {path}", error);
            return null;
        }
    }

    public static void Delete(string libraryRoot, string clipPath)
    {
        RecordingFileOwnership.ThrowIfActive(clipPath);
        try { File.Delete(FullSessionRecovery.Marker(clipPath)); } catch (IOException) { }
        SpotifyTimelineSidecar.Delete(libraryRoot, clipPath);
        try
        {
            // Overlay captures are owned by this clip, unlike Spotify artwork
            // which lives in a content-addressed archive. Remove them before
            // dropping the only manifest that can identify them.
            // One corrupt/locked asset must not prevent attempts for other
            // owned assets. Distinct also covers an input index shared through
            // AssetPath and InputIndexPath in older captures.
            foreach (var asset in ClipOverlayManifest.ExistingOwnedAssetPaths(libraryRoot, clipPath, Load(libraryRoot, clipPath)?.OverlayManifest)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { File.Delete(asset); }
                catch (Exception error) { AppLog.Error($"Overlay asset delete failed: {asset}", error); }
            }
            var paths = new[] { SidecarPath(libraryRoot, clipPath), LibraryLayout.LegacySidecarPath(clipPath, ".info.json") };
            foreach (var path in paths.Where(path => LibraryPathGuard.IsWithin(libraryRoot, path) && File.Exists(path))) File.Delete(path);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip info sidecar delete failed: {clipPath}", error);
        }
    }

    // Sidecars are a few hundred bytes. A library refresh reads one per clip, so an
    // oversized file - however it got there - should be skipped rather than loaded.
    private const long MaximumSidecarBytes = 64 * 1024;

    private static string? ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumSidecarBytes) return null;
        return File.ReadAllText(path);
    }
}
