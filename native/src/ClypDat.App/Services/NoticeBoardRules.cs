using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

public sealed record NoticeLink(string Label, string Url);

public sealed record Notice(
    string Id,
    string Severity,
    string Title,
    string Body,
    DateTimeOffset PublishedAt,
    DateTimeOffset? ExpiresAt,
    string? MinVersion,
    string? MaxVersion,
    NoticeLink? Link)
{
    public bool IsCritical => Severity == "critical";
}

public sealed record NoticeFeed(DateTimeOffset IssuedAt, IReadOnlyList<Notice> Notices);

public sealed record KillSwitch(
    string Id, string Control, string? Target, string Reason,
    string? MinVersion, string? MaxVersion, DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt);

public sealed record NoticeFeedPolicy(long Revision, DateTimeOffset IssuedAt, IReadOnlyList<Notice> Notices, IReadOnlyList<KillSwitch> Switches);

/// <summary>
/// Everything about the Notice Board that is not I/O or UI, so it can be tested on
/// its own (NoticeBoardRulesTests). The feed is written at www.clypdat.xyz/admin and
/// signed there with the notice key; see NoticeSigning.cs and the webapp's
/// app/lib/notice-feed.ts for the other half of this contract.
/// </summary>
internal static class NoticeBoardRules
{
    // Generous bounds on a feed we sign ourselves - a feed outside them is not one
    // the admin page could have produced, so it is refused rather than trimmed.
    public const int MaxEnvelopeBytes = 256 * 1024;
    private const int MaxNotices = 50;
    private const int MaxSwitches = 50;
    private const int MaxReason = 1000;
    private const int MaxTitle = 200;
    private const int MaxBody = 8000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Envelope(string? Payload, string? Signature);
    private sealed record PayloadDto(int Schema, DateTimeOffset IssuedAt, List<NoticeDto>? Notices, long Revision = 0, JsonElement Flags = default);
    private sealed record NoticeDto(
        string? Id, string? Severity, string? Title, string? Body,
        DateTimeOffset PublishedAt, DateTimeOffset? ExpiresAt,
        string? MinVersion, string? MaxVersion, LinkDto? Link);
    private sealed record LinkDto(string? Label, string? Url);

    private static readonly HashSet<string> SupportedControls = new(StringComparer.Ordinal)
    {
        "pause-auto-clipping", "disable-game-detector", "pause-spotify", "pause-xbox-activity",
        "pause-discord-presence", "block-update-version"
    };
    private static readonly HashSet<string> SupportedGameIds = new(StringComparer.OrdinalIgnoreCase)
        { "cs2", "dota2", "fortnite", "helldivers2", "league", "overwatch" };

    /// <summary>
    /// Verifies the envelope's signature against <paramref name="trustedKeys"/> and
    /// returns the feed. Throws on anything that does not verify or parse - an
    /// unverified feed is not a feed, and the caller keeps what it had.
    /// </summary>
    public static NoticeFeed ParseAndVerify(string envelopeJson, IReadOnlyList<PinnedReleaseKey> trustedKeys)
    {
        if (Encoding.UTF8.GetByteCount(envelopeJson) > MaxEnvelopeBytes) throw new InvalidDataException("Notice feed is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
            ?? throw new InvalidDataException("Notice feed was empty.");
        if (string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
            throw new InvalidDataException("Notice feed is missing its payload or signature.");

        byte[] payload;
        try { payload = Convert.FromBase64String(envelope.Payload); }
        catch (FormatException error) { throw new InvalidDataException("Notice feed payload is not base64.", error); }
        ReleaseSigning.VerifyDetached(payload, Encoding.UTF8.GetBytes(envelope.Signature), trustedKeys, "Notice feed");

        var dto = JsonSerializer.Deserialize<PayloadDto>(payload, JsonOptions)
            ?? throw new InvalidDataException("Notice feed payload was empty.");
        if (dto.Schema != 1) throw new InvalidDataException($"Unsupported notice feed schema {dto.Schema}.");
        var notices = dto.Notices ?? new List<NoticeDto>();
        if (notices.Count > MaxNotices) throw new InvalidDataException("Notice feed lists too many notices.");

        var parsed = new List<Notice>(notices.Count);
        foreach (var notice in notices)
        {
            if (string.IsNullOrWhiteSpace(notice.Id) || string.IsNullOrWhiteSpace(notice.Title) || string.IsNullOrWhiteSpace(notice.Body)) continue;
            if (notice.Title.Length > MaxTitle || notice.Body.Length > MaxBody) continue;
            // A severity this build does not know shows as plain info, never as
            // something louder than the admin page could have meant.
            var severity = notice.Severity is "feature" or "info" or "critical" ? notice.Severity : "info";
            var link = notice.Link is { Url: { } url } && IsAllowedLink(url)
                ? new NoticeLink(string.IsNullOrWhiteSpace(notice.Link.Label) ? "Read more" : notice.Link.Label!, url)
                : null;
            parsed.Add(new Notice(notice.Id, severity, notice.Title, notice.Body, notice.PublishedAt, notice.ExpiresAt,
                notice.MinVersion, notice.MaxVersion, link));
        }
        ParseSwitches(dto.Flags);
        return new NoticeFeed(dto.IssuedAt, parsed);
    }

    public static NoticeFeedPolicy ParsePolicy(string envelopeJson, IReadOnlyList<PinnedReleaseKey> trustedKeys)
    {
        if (Encoding.UTF8.GetByteCount(envelopeJson) > MaxEnvelopeBytes) throw new InvalidDataException("Notice feed is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
            ?? throw new InvalidDataException("Notice feed was empty.");
        if (string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature)) throw new InvalidDataException("Notice feed is missing its payload or signature.");
        byte[] payload;
        try { payload = Convert.FromBase64String(envelope.Payload); } catch (FormatException error) { throw new InvalidDataException("Notice feed payload is not base64.", error); }
        ReleaseSigning.VerifyDetached(payload, Encoding.UTF8.GetBytes(envelope.Signature), trustedKeys, "Notice feed");
        var dto = JsonSerializer.Deserialize<PayloadDto>(payload, JsonOptions) ?? throw new InvalidDataException("Notice feed payload was empty.");
        if (dto.Schema != 1) throw new InvalidDataException($"Unsupported notice feed schema {dto.Schema}.");
        var feed = ParseNotices(dto);
        return new NoticeFeedPolicy(dto.Revision, feed.IssuedAt, feed.Notices, ParseSwitches(dto.Flags));
    }

    private static NoticeFeed ParseNotices(PayloadDto dto)
    {
        var notices = dto.Notices ?? new List<NoticeDto>();
        if (notices.Count > MaxNotices) throw new InvalidDataException("Notice feed lists too many notices.");
        var parsed = new List<Notice>(notices.Count);
        foreach (var notice in notices)
        {
            if (string.IsNullOrWhiteSpace(notice.Id) || string.IsNullOrWhiteSpace(notice.Title) || string.IsNullOrWhiteSpace(notice.Body)) continue;
            if (notice.Title.Length > MaxTitle || notice.Body.Length > MaxBody) continue;
            var severity = notice.Severity is "feature" or "info" or "critical" ? notice.Severity : "info";
            var link = notice.Link is { Url: { } url } && IsAllowedLink(url)
                ? new NoticeLink(string.IsNullOrWhiteSpace(notice.Link.Label) ? "Read more" : notice.Link.Label!, url) : null;
            parsed.Add(new Notice(notice.Id, severity, notice.Title, notice.Body, notice.PublishedAt, notice.ExpiresAt, notice.MinVersion, notice.MaxVersion, link));
        }
        return new NoticeFeed(dto.IssuedAt, parsed);
    }

    private static IReadOnlyList<KillSwitch> ParseSwitches(JsonElement flags)
    {
        if (flags.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Array.Empty<KillSwitch>();
        if (flags.ValueKind == JsonValueKind.Object && flags.EnumerateObject().Any()) throw new InvalidDataException("Notice feed flags must be an array or legacy empty object.");
        if (flags.ValueKind == JsonValueKind.Object) return Array.Empty<KillSwitch>();
        if (flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() > MaxSwitches) throw new InvalidDataException("Notice feed lists too many or malformed switches.");
        var result = new List<KillSwitch>();
        foreach (var item in flags.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || !item.TryGetProperty("control", out var control) ||
                !item.TryGetProperty("reason", out var reason) || !item.TryGetProperty("expiresAt", out var expires)) continue;
            var name = control.GetString();
            if (name is null || !SupportedControls.Contains(name)) continue;
            var target = item.TryGetProperty("target", out var targetElement) && targetElement.ValueKind != JsonValueKind.Null ? targetElement.GetString() : null;
            if (name == "disable-game-detector" && (target is null || !SupportedGameIds.Contains(target))) continue;
            if (name == "block-update-version" && (target is null || !TryParseStableVersion(target, out _))) continue;
            if (name != "disable-game-detector" && name != "block-update-version" && target is not null) continue;
            var why = reason.GetString();
            if (string.IsNullOrWhiteSpace(why) || why.Length > MaxReason || !DateTimeOffset.TryParse(expires.GetString(), out var expiry) || expiry <= DateTimeOffset.UtcNow) continue;
            var min = OptionalVersion(item, "minVersion");
            var max = OptionalVersion(item, "maxVersion");
            if (item.TryGetProperty("minVersion", out var minElement) && minElement.ValueKind == JsonValueKind.String && min is null) continue;
            if (item.TryGetProperty("maxVersion", out var maxElement) && maxElement.ValueKind == JsonValueKind.String && max is null) continue;
            if (min is not null && max is not null && new Version(min) > new Version(max)) continue;
            var published = item.TryGetProperty("publishedAt", out var publishedElement) && DateTimeOffset.TryParse(publishedElement.GetString(), out var publishedAt) ? publishedAt : DateTimeOffset.MinValue;
            result.Add(new KillSwitch(id.GetString()!, name, target, why, min, max, published, expiry));
        }
        return result;
    }

    private static string? OptionalVersion(JsonElement item, string name) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : TryParseStableVersion(value.GetString() ?? string.Empty, out _) ? value.GetString() : null;

    private static bool TryParseStableVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        return Version.TryParse(value, out var parsed) && parsed.Revision < 0 && parsed.Build >= 0 && value.Count(c => c == '.') == 2 && (version = new Version(parsed.Major, parsed.Minor, parsed.Build)) is not null;
    }

    /// <summary>
    /// Rollback guard: a feed older than the one already held is refused, so a
    /// replayed old (validly signed) feed cannot hide a critical notice.
    /// </summary>
    public static bool ShouldReplace(NoticeFeedPolicy? current, NoticeFeedPolicy candidate) =>
        current is null || candidate.Revision > current.Revision || (candidate.Revision == current.Revision && candidate.IssuedAt >= current.IssuedAt);

    public static bool ShouldReplace(NoticeFeed? current, NoticeFeed candidate) =>
        current is null || candidate.IssuedAt >= current.IssuedAt;

    public static IReadOnlyList<KillSwitch> ActiveSwitches(NoticeFeedPolicy? feed, Version currentVersion, DateTimeOffset now) =>
        feed is null ? Array.Empty<KillSwitch>() : feed.Switches.Where(item => item.ExpiresAt > now)
            .Where(item => !TryParseStableVersion(item.MinVersion ?? string.Empty, out var min) || currentVersion >= min)
            .Where(item => !TryParseStableVersion(item.MaxVersion ?? string.Empty, out var max) || currentVersion <= max)
            .ToList();

    public static bool IsBlocked(IReadOnlyList<KillSwitch> switches, string control, string? target = null) =>
        switches.Any(item => string.Equals(item.Control, control, StringComparison.Ordinal) &&
            (item.Target is null || string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Notices meant for this version and not yet expired, newest first.</summary>
    public static IReadOnlyList<Notice> Applicable(NoticeFeed? feed, Version currentVersion, DateTimeOffset now)
    {
        if (feed is null) return Array.Empty<Notice>();
        var current = new Version(Math.Max(0, currentVersion.Major), Math.Max(0, currentVersion.Minor), Math.Max(0, currentVersion.Build));
        return feed.Notices
            .Where(notice => notice.ExpiresAt is not { } expires || expires > now)
            .Where(notice => !TryParseVersion(notice.MinVersion, out var min) || current >= min)
            .Where(notice => !TryParseVersion(notice.MaxVersion, out var max) || current <= max)
            .OrderByDescending(notice => notice.PublishedAt)
            .ToList();
    }

    /// <summary>What should pop up now: anything not seen yet, plus critical notices not yet acknowledged.</summary>
    public static IReadOnlyList<Notice> ToShow(IReadOnlyList<Notice> applicable, IEnumerable<string> seenIds, IEnumerable<string> acknowledgedIds)
    {
        var seen = new HashSet<string>(seenIds, StringComparer.OrdinalIgnoreCase);
        var acknowledged = new HashSet<string>(acknowledgedIds, StringComparer.OrdinalIgnoreCase);
        return applicable
            .Where(notice => notice.IsCritical ? !acknowledged.Contains(notice.Id) : !seen.Contains(notice.Id))
            .ToList();
    }

    public static bool HasUnread(IReadOnlyList<Notice> applicable, IEnumerable<string> seenIds, IEnumerable<string> acknowledgedIds) =>
        ToShow(applicable, seenIds, acknowledgedIds).Count > 0;

    /// <summary>Info and critical notices interrupt a session once; features wait until launch.</summary>
    public static IReadOnlyList<Notice> ToShowDuringSession(IReadOnlyList<Notice> applicable,
        IEnumerable<string> seenIds, IEnumerable<string> acknowledgedIds, IEnumerable<string> poppedThisSession)
    {
        var popped = new HashSet<string>(poppedThisSession, StringComparer.OrdinalIgnoreCase);
        return ToShow(applicable, seenIds, acknowledgedIds)
            .Where(notice => notice.Severity is "info" or "critical" && !popped.Contains(notice.Id))
            .ToList();
    }

    /// <summary>
    /// Where a notice may send someone. Enforced here even though the site checks the
    /// same list, so a hijacked admin account still cannot turn a notice into a link
    /// to anywhere else.
    /// </summary>
    public static bool IsAllowedLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host == "clypdat.xyz" || host.EndsWith(".clypdat.xyz", StringComparison.Ordinal)) return true;
        if (host == "github.com") return uri.AbsolutePath == "/ClypLabs" || uri.AbsolutePath.StartsWith("/ClypLabs/", StringComparison.Ordinal);
        if (host == "discord.gg") return uri.AbsolutePath == "/jt3eJf238t";
        return false;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }
}
