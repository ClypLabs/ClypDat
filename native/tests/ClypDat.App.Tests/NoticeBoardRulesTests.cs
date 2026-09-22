using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NoticeBoardRulesTests
{
    private static readonly RSA SigningKey = RSA.Create(2048);
    private static readonly IReadOnlyList<PinnedReleaseKey> TrustedKeys = new[]
    {
        new PinnedReleaseKey("test", Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo()))
    };

    private static object NoticeJson(string id, string severity = "feature", string? min = null, string? max = null,
        string? expires = null, string published = "2027-01-01T00:00:00Z", object? link = null) => new
    {
        id, severity, title = $"Title {id}", body = "Body", publishedAt = published, expiresAt = expires,
        minVersion = min, maxVersion = max, link
    };

    // Signed exactly as the site signs it: RSA-PSS, SHA-256, 32-byte salt
    // (clypdat-webapp app/lib/notice-feed.ts).
    private static string Envelope(string issuedAt, params object[] notices) => Envelope(issuedAt, SigningKey, notices);

    private static string Envelope(string issuedAt, RSA key, params object[] notices)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schema = 1, issuedAt, notices, flags = new { } }));
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }

    [Fact]
    public void VerifiesAndParsesSignedFeed()
    {
        var feed = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z", NoticeJson("a", "critical")), TrustedKeys);
        Assert.Equal(DateTimeOffset.Parse("2027-01-02T00:00:00Z"), feed.IssuedAt);
        var notice = Assert.Single(feed.Notices);
        Assert.Equal("a", notice.Id);
        Assert.True(notice.IsCritical);
    }

    [Fact]
    public void RejectsTamperedPayload()
    {
        var envelope = JsonSerializer.Deserialize<Dictionary<string, string>>(Envelope("2027-01-02T00:00:00Z", NoticeJson("a")))!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope["payload"])).Replace("Title a", "Title b");
        envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        Assert.Throws<CryptographicException>(() => NoticeBoardRules.ParseAndVerify(JsonSerializer.Serialize(envelope), TrustedKeys));
    }

    [Fact]
    public void RejectsFeedSignedByAnotherKey()
    {
        using var other = RSA.Create(2048);
        Assert.Throws<CryptographicException>(() => NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z", other, NoticeJson("a")), TrustedKeys));
    }

    [Fact]
    public void ReleaseKeysDoNotVerifyNotices()
    {
        Assert.Throws<CryptographicException>(() => NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z", NoticeJson("a")), ReleaseSigning.PinnedPublicKeys));
    }

    [Fact]
    public void RollbackToOlderFeedIsRefused()
    {
        var newer = NoticeBoardRules.ParseAndVerify(Envelope("2027-02-01T00:00:00Z", NoticeJson("a", "critical")), TrustedKeys);
        var older = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-01T00:00:00Z"), TrustedKeys);
        Assert.True(NoticeBoardRules.ShouldReplace(null, older));
        Assert.False(NoticeBoardRules.ShouldReplace(newer, older));
        Assert.True(NoticeBoardRules.ShouldReplace(older, newer));
    }

    [Fact]
    public void FiltersByVersionRangeAndExpiry()
    {
        var feed = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z",
            NoticeJson("everyone", published: "2027-01-01T00:00:00Z"),
            NoticeJson("old-builds", max: "1.5.3", published: "2027-01-02T00:00:00Z"),
            NoticeJson("this-range", min: "1.5.0", max: "1.5.4", published: "2027-01-03T00:00:00Z"),
            NoticeJson("future", min: "1.6.0"),
            NoticeJson("expired", expires: "2027-01-01T12:00:00Z")), TrustedKeys);
        var applicable = NoticeBoardRules.Applicable(feed, new Version(1, 5, 4, 0), DateTimeOffset.Parse("2027-01-05T00:00:00Z"));
        Assert.Equal(new[] { "this-range", "everyone" }, applicable.Select(notice => notice.Id));
    }

    [Fact]
    public void CriticalNoticesRepeatUntilAcknowledged()
    {
        var feed = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z", NoticeJson("feature"), NoticeJson("urgent", "critical")), TrustedKeys);
        var applicable = NoticeBoardRules.Applicable(feed, new Version(1, 5, 4), DateTimeOffset.Parse("2027-01-05T00:00:00Z"));
        Assert.Equal(2, NoticeBoardRules.ToShow(applicable, [], []).Count);
        var afterSeen = NoticeBoardRules.ToShow(applicable, ["feature", "urgent"], []);
        Assert.Equal("urgent", Assert.Single(afterSeen).Id);
        Assert.Empty(NoticeBoardRules.ToShow(applicable, ["feature", "urgent"], ["urgent"]));
    }

    [Fact]
    public void ActiveSessionShowsInfoAndCriticalButDefersFeatures()
    {
        var feed = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z",
            NoticeJson("feature"), NoticeJson("info", "info"), NoticeJson("critical", "critical")), TrustedKeys);
        var applicable = NoticeBoardRules.Applicable(feed, new Version(1, 5, 4), DateTimeOffset.Parse("2027-01-05T00:00:00Z"));
        Assert.Equal(new[] { "info", "critical" }, NoticeBoardRules.ToShowDuringSession(applicable, [], [], []).Select(n => n.Id));
        // A blocked dialog must leave these eligible on the next tick, even with the same feed.
        Assert.Equal(2, NoticeBoardRules.ToShowDuringSession(applicable, [], [], []).Count);
        Assert.Empty(NoticeBoardRules.ToShowDuringSession(applicable, ["INFO"], [], ["CRITICAL"]));
        Assert.Empty(NoticeBoardRules.ToShowDuringSession(applicable, ["info"], ["critical"], []));
        // On a new launch an unacknowledged critical notice returns, but seen info does not.
        Assert.Equal("critical", Assert.Single(NoticeBoardRules.ToShowDuringSession(applicable, ["info"], [], [])).Id);
        Assert.Contains(NoticeBoardRules.ToShow(applicable, ["info"], ["critical"]), n => n.Id == "feature");
    }

    [Fact]
    public void UnknownSeverityIsDowngradedAndDisallowedLinksDropped()
    {
        var feed = NoticeBoardRules.ParseAndVerify(Envelope("2027-01-02T00:00:00Z",
            NoticeJson("a", "emergency", link: new { label = "Go", url = "https://evil.example/login" }),
            NoticeJson("b", link: new { label = "Read", url = "https://www.clypdat.xyz/news" })), TrustedKeys);
        Assert.Equal("info", feed.Notices[0].Severity);
        Assert.Null(feed.Notices[0].Link);
        Assert.Equal("https://www.clypdat.xyz/news", feed.Notices[1].Link?.Url);
    }

    [Theory]
    [InlineData("https://www.clypdat.xyz/blog", true)]
    [InlineData("https://clypdat.xyz", true)]
    [InlineData("https://github.com/ClypLabs/ClypDat/releases", true)]
    [InlineData("https://discord.gg/jt3eJf238t", true)]
    [InlineData("http://www.clypdat.xyz", false)]
    [InlineData("https://clypdat.xyz.evil.test", false)]
    [InlineData("https://evilclypdat.xyz", false)]
    [InlineData("https://github.com/ClypLabsX", false)]
    [InlineData("https://github.com/someone/ClypLabs", false)]
    [InlineData("https://discord.gg/other", false)]
    [InlineData("https://user@www.clypdat.xyz", false)]
    [InlineData("https://www.clypdat.xyz:8443/", false)]
    [InlineData("javascript:alert(1)", false)]
    public void LinksAreLimitedToClypDatPlaces(string url, bool allowed) =>
        Assert.Equal(allowed, NoticeBoardRules.IsAllowedLink(url));

    [Fact]
    public void PinnedNoticeKeyIsNotAReleaseKey()
    {
        var release = ReleaseSigning.PinnedPublicKeys.Select(key => key.SubjectPublicKeyInfoBase64).ToHashSet();
        Assert.NotEmpty(NoticeSigning.PinnedPublicKeys);
        Assert.DoesNotContain(NoticeSigning.PinnedPublicKeys, key => release.Contains(key.SubjectPublicKeyInfoBase64));
    }

    // Kill switches travel in the same signed payload as notices, as a `flags` array.
    private static string PolicyEnvelope(long revision, object flags, string issuedAt = "2027-01-02T00:00:00Z")
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schema = 1, issuedAt, revision, notices = Array.Empty<object>(), flags }));
        var signature = SigningKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }

    private static object Switch(string id, string control, string? target = null, string? min = null, string? max = null, string? expires = null) => new
    {
        id, control, target, reason = "testing", minVersion = min, maxVersion = max,
        publishedAt = "2027-01-01T00:00:00Z",
        expiresAt = expires ?? DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
    };

    [Fact]
    public void SignedSwitchesBlockTheirControlAndTarget()
    {
        var policy = NoticeBoardRules.ParsePolicy(PolicyEnvelope(3, new[]
        {
            Switch("x", "pause-xbox-activity"),
            Switch("d", "disable-game-detector", target: "cs2"),
            Switch("u", "block-update-version", target: "1.5.5"),
        }), TrustedKeys);
        Assert.Equal(3, policy.Revision);
        var active = NoticeBoardRules.ActiveSwitches(policy, new Version(1, 5, 4), DateTimeOffset.UtcNow);
        Assert.True(NoticeBoardRules.IsBlocked(active, "pause-xbox-activity"));
        Assert.False(NoticeBoardRules.IsBlocked(active, "pause-spotify"));
        Assert.True(NoticeBoardRules.IsBlocked(active, "disable-game-detector", "CS2"));
        Assert.False(NoticeBoardRules.IsBlocked(active, "disable-game-detector", "dota2"));
        Assert.True(NoticeBoardRules.IsBlocked(active, "block-update-version", "1.5.5"));
        Assert.False(NoticeBoardRules.IsBlocked(active, "block-update-version", "1.5.6"));
    }

    [Fact]
    public void SwitchesRespectVersionRangeAndExpiry()
    {
        var policy = NoticeBoardRules.ParsePolicy(PolicyEnvelope(1, new[]
        {
            Switch("old", "pause-spotify", max: "1.5.3"),
            Switch("range", "pause-xbox-activity", min: "1.5.0", max: "1.5.4"),
            Switch("soon", "pause-discord-presence", expires: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")),
        }), TrustedKeys);
        var now = NoticeBoardRules.ActiveSwitches(policy, new Version(1, 5, 4), DateTimeOffset.UtcNow);
        Assert.False(NoticeBoardRules.IsBlocked(now, "pause-spotify"));
        Assert.True(NoticeBoardRules.IsBlocked(now, "pause-xbox-activity"));
        Assert.True(NoticeBoardRules.IsBlocked(now, "pause-discord-presence"));
        // Expiry is enforced on the client, so a held feed stops blocking on time.
        var later = NoticeBoardRules.ActiveSwitches(policy, new Version(1, 5, 4), DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.False(NoticeBoardRules.IsBlocked(later, "pause-discord-presence"));
    }

    [Fact]
    public void MalformedSwitchesAreDroppedNotApplied()
    {
        var policy = NoticeBoardRules.ParsePolicy(PolicyEnvelope(1, new[]
        {
            Switch("unknown", "wipe-everything"),
            Switch("no-target", "disable-game-detector"),
            Switch("bad-game", "disable-game-detector", target: "minecraft"),
            Switch("target-not-allowed", "pause-spotify", target: "cs2"),
            Switch("fuzzy-version", "block-update-version", target: "1.5"),
            Switch("expired", "pause-spotify", expires: "2020-01-01T00:00:00Z"),
        }), TrustedKeys);
        Assert.Empty(policy.Switches);
    }

    [Fact]
    public void FlagsObjectWithContentIsRejectedButLegacyEmptyObjectIsFine()
    {
        Assert.Empty(NoticeBoardRules.ParsePolicy(PolicyEnvelope(0, new { }), TrustedKeys).Switches);
        Assert.Throws<InvalidDataException>(() => NoticeBoardRules.ParsePolicy(PolicyEnvelope(0, new { autoClip = false }), TrustedKeys));
    }

    [Fact]
    public void RevisionDecidesReplacementBeforeIssueTime()
    {
        var current = NoticeBoardRules.ParsePolicy(PolicyEnvelope(3, Array.Empty<object>(), "2027-01-02T00:00:00Z"), TrustedKeys);
        var reissued = NoticeBoardRules.ParsePolicy(PolicyEnvelope(3, Array.Empty<object>(), "2027-01-03T00:00:00Z"), TrustedKeys);
        var olderRevision = NoticeBoardRules.ParsePolicy(PolicyEnvelope(2, Array.Empty<object>(), "2027-02-01T00:00:00Z"), TrustedKeys);
        var newerRevision = NoticeBoardRules.ParsePolicy(PolicyEnvelope(4, Array.Empty<object>(), "2027-01-01T00:00:00Z"), TrustedKeys);
        Assert.True(NoticeBoardRules.ShouldReplace(current, reissued));
        Assert.False(NoticeBoardRules.ShouldReplace(current, olderRevision));
        Assert.True(NoticeBoardRules.ShouldReplace(current, newerRevision));
    }
}
