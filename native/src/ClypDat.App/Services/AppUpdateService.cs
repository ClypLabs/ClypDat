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
    // Lowercase hex SHA-256 of the asset as GitHub computed it, or empty when
    // the API didn't report one (older releases predate the field).
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

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    // Last ETag and body seen per releases URL, so a repeat check can send
    // If-None-Match and take a header-only 304 instead of re-downloading a
    // payload that hasn't changed. The update check runs on a timer for the
    // whole life of the app and almost always finds nothing new, so nearly
    // every one of those requests was re-fetching identical bytes - 10KB for
    // releases/latest, and 126KB for the full release list behind the notes.
    //
    // Two entries at most, both small, held for the process lifetime. Static
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
    private static async Task<string?> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var hasCached = ConditionalCache.TryGetValue(url, out var cached);
        if (hasCached) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && hasCached) return cached.Body;
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // GitHub's ETags are weak ("W/..."), which If-None-Match accepts as-is.
        var etag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(etag)) ConditionalCache[url] = (etag, body);
        return body;
    }

    public static async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var json = await GetJsonAsync(client, LatestReleaseUrl, cancellationToken);
        if (json is null) return null;
        var release = JsonSerializer.Deserialize<ReleaseResponse>(json);
        if (release is null || release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var latest) || latest <= CurrentVersion)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(item => item.Name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || !IsTrustedReleaseAssetUrl(asset.DownloadUrl))
        {
            return null;
        }

        var (whatsNew, fixes) = await LoadReleaseNotesAsync(client, latest, cancellationToken);
        return new AppUpdateInfo(CurrentVersion, latest, release.TagName, asset.DownloadUrl, whatsNew, fixes, ParseSha256Digest(asset.Digest));
    }

    public static async Task DownloadAndRestartAsync(AppUpdateInfo update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "updates");
        Directory.CreateDirectory(updateRoot);
        var setupPath = Path.Combine(updateRoot, $"ClypDat-Setup-{update.LatestVersion}.exe");

        using var client = CreateClient();
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
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
            var json = await GetJsonAsync(client, ReleasesUrl, cancellationToken);
            var releases = (json is null ? null : JsonSerializer.Deserialize<ReleaseResponse[]>(json)) ?? [];
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
            var json = await GetJsonAsync(client, ReleasesUrl, cancellationToken);
            var releases = (json is null ? null : JsonSerializer.Deserialize<ReleaseResponse[]>(json)) ?? [];
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

    internal static bool IsTrustedReleaseAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith($"/{UpstreamOwner}/{UpstreamRepository}/releases/download/", StringComparison.OrdinalIgnoreCase);
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

    // GitHub reports asset digests as "sha256:<hex>". Anything else (or a
    // missing value) is treated as "no digest available" rather than trusted.
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

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest = null);
}
