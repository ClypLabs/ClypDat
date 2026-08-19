using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

public sealed record AppUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string DownloadUrl,
    IReadOnlyList<string> WhatsNew,
    IReadOnlyList<string> Fixes,
    // Lowercase hex SHA-256 of the asset as reported by the release API, or
    // empty when the API didn't report one.
    string Sha256 = "");

public sealed record UpdateDownloadProgress(string Status, double? Percentage, double? BytesPerSecond = null);

public static class AppUpdateService
{
    // Updates use the NSIS installer rather than replacing files in place, so
    // it can safely replace the running app after ClypDat exits.
    private const string ExpectedAssetName = "ClypDat-Setup.exe";
    private const string UpstreamOwner = "ClypDat";
    private const string UpstreamRepository = "ClypDat";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{UpstreamOwner}/{UpstreamRepository}/releases/latest";
    private const string ReleasesUrl = $"https://api.github.com/repos/{UpstreamOwner}/{UpstreamRepository}/releases?per_page=100";

    // GitLab is a direct fallback for release metadata and installer assets.
    // Keep this project path aligned with the GitLab mirror repository.
    private const string GitLabProjectPath = "clypdat-group1/ClypDat-App";
    private const string GitLabProjectId = "clypdat-group1%2FClypDat-App";
    private const string GitLabHost = "gitlab.com";
    private const string GitLabLatestReleaseUrl = $"https://{GitLabHost}/api/v4/projects/{GitLabProjectId}/releases/permalink/latest";
    private const string GitLabReleasesUrl = $"https://{GitLabHost}/api/v4/projects/{GitLabProjectId}/releases?per_page=100";

    // Mirror on the marketing site, serving the same JSON shape from Cloudflare
    // R2. Exists so installs and updates survive GitHub being unreachable -
    // an outage, or the account being flagged. It remains a final safety net
    // after the direct GitHub and GitLab sources.
    private const string MirrorHost = "www.clypdat.xyz";
    private const string MirrorApexHost = "clypdat.xyz";
    private const string MirrorLatestReleaseUrl = $"https://{MirrorHost}/api/releases/latest";
    private const string MirrorReleasesUrl = $"https://{MirrorHost}/api/releases";

    // Set by --force-mirror to skip GitHub entirely. A fallback path that only
    // runs during a GitHub outage is a path you discover is broken during a
    // GitHub outage; this makes it testable on demand.
    public static bool ForceMirror { get; set; } =
        Environment.GetCommandLineArgs().Any(argument =>
            argument.Equals("--force-mirror", StringComparison.OrdinalIgnoreCase));

    // Ordered source list. GitHub primary, GitLab fallback, mirror last. All
    // sources are checked so a newer GitLab release is found when GitHub is
    // reachable but its release workflow failed.
    private static IEnumerable<ReleaseSource> LatestReleaseSources =>
        ForceMirror
            ? [new(MirrorLatestReleaseUrl, ReleaseSourceKind.Mirror)]
            : [
                new(LatestReleaseUrl, ReleaseSourceKind.GitHub),
                new(GitLabLatestReleaseUrl, ReleaseSourceKind.GitLab),
                new(MirrorLatestReleaseUrl, ReleaseSourceKind.Mirror),
            ];

    private static IEnumerable<ReleaseSource> ReleaseListSources =>
        ForceMirror
            ? [new(MirrorReleasesUrl, ReleaseSourceKind.Mirror)]
            : [
                new(ReleasesUrl, ReleaseSourceKind.GitHub),
                new(GitLabReleasesUrl, ReleaseSourceKind.GitLab),
                new(MirrorReleasesUrl, ReleaseSourceKind.Mirror),
            ];

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    // Last ETag and body seen per releases URL, so a repeat check can send
    // If-None-Match and take a header-only 304 instead of re-downloading a
    // payload that hasn't changed. The update check runs on a timer for the
    // whole life of the app and almost always finds nothing new, so nearly
    // every one of those requests was re-fetching identical bytes - 10KB for
    // releases/latest, and 126KB for the full release list behind the notes.
    //
    // Six entries at most, all small, held for the process lifetime. Static
    // because CreateClient builds a fresh HttpClient per call, so there is no
    // longer-lived object to hang this off.
    //
    // NOTE: a 304 still costs one request against GitHub's 60/hour
    // unauthenticated rate limit - verified against X-RateLimit-Remaining,
    // which decrements on 304s too. This saves bandwidth, latency and the
    // deserialize; the poll interval is what protects the rate limit.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string ETag, string Body)> ConditionalCache = new();

    // GET url, conditionally. Returns the response body, either fresh or the
    // cached copy a 304 just confirmed is still current. Null when the request
    // failed outright, so callers keep their existing failure behaviour rather
    // than parsing an empty string.
    //
    // ShouldFailOver says whether a source failure should be logged as an
    // unavailable source. Every source is still queried because a later source
    // may contain a newer release even when an earlier source answers.
    //
    // 404 qualifies too. It reads like a definitive "no release exists", but a
    // flagged repository is served as 404 to anonymous callers while still
    // reporting public to the owner - so the one failure this mirror was built
    // for is indistinguishable from an empty repo. The cost of being wrong is
    // one wasted request to the mirror; the cost of not failing over is every
    // user silently stuck on their installed version.
    private static async Task<(string? Body, bool ShouldFailOver)> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var hasCached = ConditionalCache.TryGetValue(url, out var cached);
            if (hasCached) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && hasCached) return (cached.Body, false);
            if (!response.IsSuccessStatusCode) return (null, IsFailOverStatus(response.StatusCode));

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // GitHub's ETags are weak ("W/..."), which If-None-Match accepts as-is.
            var etag = response.Headers.ETag?.ToString();
            if (!string.IsNullOrEmpty(etag)) ConditionalCache[url] = (etag, body);
            return (body, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; do not burn the fallback on a cancelled check.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Unreachable, DNS failure, TLS failure, or the per-request timeout.
            return (null, true);
        }
    }

    private static bool IsFailOverStatus(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.Forbidden or        // GitHub reports rate limiting as 403
        System.Net.HttpStatusCode.NotFound or         // and a flagged repository as 404
        System.Net.HttpStatusCode.TooManyRequests or
        System.Net.HttpStatusCode.RequestTimeout or
        >= System.Net.HttpStatusCode.InternalServerError;

    // Queries every source in order. GitLab's release schema is normalized to
    // ReleaseResponse before callers inspect it.
    private static async Task<IReadOnlyList<T>> GetJsonFromAllSourcesAsync<T>(
        HttpClient client,
        IEnumerable<ReleaseSource> sources,
        Func<string, ReleaseSourceKind, T?> parser,
        CancellationToken cancellationToken) where T : class
    {
        var results = new List<T>();
        string? lastFailed = null;
        foreach (var source in sources)
        {
            var (body, shouldFailOver) = await GetJsonAsync(client, source.Url, cancellationToken);
            if (body is not null)
            {
                try
                {
                    var parsed = parser(body, source.Kind);
                    if (parsed is not null)
                    {
                        if (lastFailed is not null)
                        {
                            AppLog.Info($"Update source {lastFailed} unavailable; also checked {source.Url}.");
                            lastFailed = null;
                        }

                        results.Add(parsed);
                    }
                }
                catch (JsonException)
                {
                    // Treat a malformed response like an unavailable source.
                }
            }

            if (body is null && shouldFailOver) lastFailed = source.Url;
        }

        return results;
    }

    public static async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var releases = await GetJsonFromAllSourcesAsync(client, LatestReleaseSources, ParseRelease, cancellationToken);
        var candidates = new List<(ReleaseResponse Release, Version Version)>();
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var version) || version <= CurrentVersion)
            {
                continue;
            }

            var asset = release.Assets.FirstOrDefault(item => item.Name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is not null && IsTrustedReleaseAssetUrl(asset.DownloadUrl))
            {
                candidates.Add((release, version));
            }
        }

        var selected = candidates.OrderByDescending(item => item.Version).FirstOrDefault();
        if (selected.Release is null)
        {
            return null;
        }

        var selectedAsset = selected.Release.Assets.First(item => item.Name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase));
        var (whatsNew, fixes) = await LoadReleaseNotesAsync(client, selected.Version, cancellationToken);
        return new AppUpdateInfo(CurrentVersion, selected.Version, selected.Release.TagName, selectedAsset.DownloadUrl, whatsNew, fixes, ParseSha256Digest(selectedAsset.Digest));
    }

    public static async Task DownloadAndRestartAsync(AppUpdateInfo update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "updates");
        Directory.CreateDirectory(updateRoot);
        var setupPath = Path.Combine(updateRoot, $"ClypDat-Setup-{update.LatestVersion}.exe");

        using var client = CreateDownloadClient();
        progress?.Report(new UpdateDownloadProgress("Downloading update...", 0));
        using (var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(setupPath);
            var buffer = new byte[81920];
            long downloaded = 0;
            var timer = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report(new UpdateDownloadProgress(
                    contentLength is > 0 ? $"Downloading update... {downloaded * 100 / contentLength}%" : "Downloading update...",
                    contentLength is > 0 ? (double)downloaded / contentLength : null,
                    timer.Elapsed.TotalSeconds > 0 ? downloaded / timer.Elapsed.TotalSeconds : null));
            }
        }

        // Verify before running the downloaded installer. The digest comes
        // from the release API, so it protects transport integrity rather
        // than a compromised release publisher.
        await VerifyDownloadAsync(setupPath, update.Sha256, cancellationToken);

        progress?.Report(new UpdateDownloadProgress("Starting installer...", 1));
        Process.Start(new ProcessStartInfo(setupPath, $"/S /UPDATEPID={Environment.ProcessId}")
        {
            UseShellExecute = true,
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // Short enough that failing over to the mirror is quick rather than a
        // half-minute freeze, long enough for a slow connection to fetch the
        // 126KB release list. The installer download uses its own client below.
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // The metadata timeout must not apply here: HttpClient.Timeout covers the
    // whole operation, response body included, so any fixed value is a cap on
    // how long a ~277MB installer may take to arrive. Cancellation is the
    // caller's CancellationToken instead.
    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-AppUpdater");
        return client;
    }

    private static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('-')[0], out version!);

    // Used by the "You're up to date" dialog to show what the currently
    // installed version actually shipped, rather than a bare version number.
    public static async Task<(IReadOnlyList<string> WhatsNew, IReadOnlyList<string> Fixes)> GetCurrentVersionNotesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            var releases = DistinctReleases(await GetJsonFromAllSourcesAsync(client, ReleaseListSources, ParseReleaseList, cancellationToken));
            // Version equality would fail here: a "v0.1.8" tag parses to a
            // 3-field Version (Revision=-1), but the assembly's CurrentVersion
            // is always 4-field (Revision=0) - compare only the 3 fields the
            // tag actually carries.
            var match = releases.FirstOrDefault(release => TryParseVersion(release.TagName, out var version) &&
                version.Major == CurrentVersion.Major && version.Minor == CurrentVersion.Minor && version.Build == CurrentVersion.Build);
            return match is null ? ([], []) : ExtractCategorizedNotes(match.Body);
        }
        catch
        {
            // Release notes are supplementary and must not block showing the dialog.
            return ([], []);
        }
    }

    private static async Task<(IReadOnlyList<string> WhatsNew, IReadOnlyList<string> Fixes)> LoadReleaseNotesAsync(HttpClient client, Version latest, CancellationToken cancellationToken)
    {
        try
        {
            var releases = DistinctReleases(await GetJsonFromAllSourcesAsync(client, ReleaseListSources, ParseReleaseList, cancellationToken));
            var whatsNew = new List<string>();
            var fixes = new List<string>();
            foreach (var item in releases
                         .Select(release => new { Release = release, Parsed = TryParseVersion(release.TagName, out var version), Version = version })
                         .Where(item => item.Parsed && !item.Release.Draft && !item.Release.Prerelease &&
                             item.Version > CurrentVersion && item.Version <= latest)
                         .OrderBy(item => item.Version))
            {
                var versionLabel = $"{item.Version.Major}.{item.Version.Minor}.{item.Version.Build}";
                var (releaseWhatsNew, releaseFixes) = ExtractCategorizedNotes(item.Release.Body);
                whatsNew.AddRange(releaseWhatsNew.Select(note => $"{versionLabel}: {note}"));
                fixes.AddRange(releaseFixes.Select(note => $"{versionLabel}: {note}"));
            }

            return (whatsNew, fixes);
        }
        catch
        {
            // Release notes are supplementary and must not block an available update.
            return ([], []);
        }
    }

    // Release bodies are expected to use "## What's New" / "## Fixes" (or
    // "## Fixed"/"## Bug Fixes") headings with "- " bullets under each, per
    // AGENTS.md's Releasing section - lets the update dialog show the two
    // apart instead of one flat mixed list. A release written before this
    // convention (or with no headings at all) has all its bullets fall
    // through to What's New, the more common case, rather than being dropped.
    private static (IReadOnlyList<string> WhatsNew, IReadOnlyList<string> Fixes) ExtractCategorizedNotes(string? body)
    {
        var whatsNew = new List<string>();
        var fixes = new List<string>();
        var current = whatsNew;

        foreach (var raw in (body ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimStart();
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#', ' ').Trim();
                if (heading.Contains("fix", StringComparison.OrdinalIgnoreCase))
                {
                    current = fixes;
                }
                else if (heading.Contains("what", StringComparison.OrdinalIgnoreCase) ||
                         heading.Contains("new", StringComparison.OrdinalIgnoreCase) ||
                         heading.Contains("feature", StringComparison.OrdinalIgnoreCase))
                {
                    current = whatsNew;
                }

                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) && line.Length > 2)
            {
                current.Add(line[2..].Trim());
            }
        }

        return (whatsNew, fixes);
    }

    // Gate on what the updater is willing to download and execute. Kept as an
    // explicit host+path allowlist rather than anything looser: this value
    // comes from a remote JSON document, and whatever passes here gets run
    // silently with /S.
    internal static bool IsTrustedReleaseAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith($"/{UpstreamOwner}/{UpstreamRepository}/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(uri.Host, GitLabHost, StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith($"/{GitLabProjectPath}/-/releases/", StringComparison.OrdinalIgnoreCase);
        }

        // Mirror. The site redirects /download/<asset> to R2; the redirect
        // target is never checked against this list, so the trust placed here
        // extends to whoever controls the site and the bucket. Apex is accepted
        // alongside www because the snapshot is only ever generated with one of
        // them and a DNS change should not silently break updates.
        var isMirrorHost = string.Equals(uri.Host, MirrorHost, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, MirrorApexHost, StringComparison.OrdinalIgnoreCase);

        return isMirrorHost && uri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // Release APIs report asset digests as "sha256:<hex>". Anything else (or
    // a missing value) is treated as "no digest available" rather than trusted.
    internal static string ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var hex = digest[prefix.Length..].Trim();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return string.Empty;
        return hex.ToLowerInvariant();
    }

    private static async Task VerifyDownloadAsync(string zipPath, string expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedSha256))
        {
            // Releases published before GitHub exposed per-asset digests have
            // none to check. Refusing them would break updating from those
            // builds entirely, so proceed but leave a record.
            AppLog.Info("Update package has no published SHA-256; skipping integrity check.");
            return;
        }

        string actual;
        await using (var stream = File.OpenRead(zipPath))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedSha256)))
        {
            TryDelete(zipPath);
            AppLog.Error($"Update package SHA-256 mismatch: expected {expectedSha256}, got {actual}. Update aborted.");
            throw new InvalidOperationException("The downloaded update failed its integrity check and was discarded.");
        }

        AppLog.Info($"Update package SHA-256 verified ({actual}).");
    }

    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] ReleaseAsset[] Assets,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);

    private enum ReleaseSourceKind
    {
        GitHub,
        GitLab,
        Mirror,
    }

    private sealed record ReleaseSource(string Url, ReleaseSourceKind Kind);

    private sealed record GitLabReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("upcoming_release")] bool UpcomingRelease,
        [property: JsonPropertyName("assets")] GitLabReleaseAssets? Assets);

    private sealed record GitLabReleaseAssets(
        [property: JsonPropertyName("links")] GitLabReleaseAsset[]? Links);

    private sealed record GitLabReleaseAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("direct_asset_url")] string? DirectAssetUrl,
        [property: JsonPropertyName("digest")] string? Digest = null);

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest = null);

    private static ReleaseResponse? ParseRelease(string json, ReleaseSourceKind sourceKind) => sourceKind == ReleaseSourceKind.GitLab
        ? NormalizeGitLabRelease(JsonSerializer.Deserialize<GitLabReleaseResponse>(json))
        : JsonSerializer.Deserialize<ReleaseResponse>(json);

    private static ReleaseResponse[]? ParseReleaseList(string json, ReleaseSourceKind sourceKind)
    {
        if (sourceKind != ReleaseSourceKind.GitLab) return JsonSerializer.Deserialize<ReleaseResponse[]>(json);

        var releases = JsonSerializer.Deserialize<GitLabReleaseResponse[]>(json);
        return releases?.Select(NormalizeGitLabRelease).Where(release => release is not null).Select(release => release!).ToArray();
    }

    private static ReleaseResponse[] DistinctReleases(IEnumerable<ReleaseResponse[]> releaseLists) =>
        releaseLists
            .SelectMany(releases => releases)
            .GroupBy(release => release.TagName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static ReleaseResponse? NormalizeGitLabRelease(GitLabReleaseResponse? release)
    {
        if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;

        var assets = (release.Assets?.Links ?? [])
            .Select(link => new ReleaseAsset(
                link.Name ?? string.Empty,
                string.IsNullOrWhiteSpace(link.DirectAssetUrl) ? link.Url ?? string.Empty : link.DirectAssetUrl,
                link.Digest))
            .ToArray();

        return new ReleaseResponse(release.TagName, assets, release.Description, release.UpcomingRelease, false);
    }
}
