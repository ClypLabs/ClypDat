using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eve.App.Services;

public sealed record AppUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string DownloadUrl,
    IReadOnlyList<string> WhatsNew,
    IReadOnlyList<string> Fixes);

public sealed record UpdateDownloadProgress(string Status, double? Percentage, double? BytesPerSecond = null);

public static class AppUpdateService
{
    // No CI pipeline publishes releases yet - `gh release create <tag> EVE-win-x64.zip`
    // (a zip of the published win-x64-folder contents) is expected to attach an asset
    // with exactly this name.
    private const string ExpectedAssetName = "EVE-win-x64.zip";
    private const string UpstreamOwner = "Stormanzanii";
    private const string UpstreamRepository = "EVE";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{UpstreamOwner}/{UpstreamRepository}/releases/latest";
    private const string ReleasesUrl = $"https://api.github.com/repos/{UpstreamOwner}/{UpstreamRepository}/releases?per_page=100";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await using var stream = await client.GetStreamAsync(LatestReleaseUrl, cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(stream, cancellationToken: cancellationToken);
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
        return new AppUpdateInfo(CurrentVersion, latest, release.TagName, asset.DownloadUrl, whatsNew, fixes);
    }

    public static async Task DownloadAndRestartAsync(AppUpdateInfo update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the running executable.");
        var installDir = Path.GetDirectoryName(currentExe) ?? throw new InvalidOperationException("Could not locate the install directory.");
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE", "updates");
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, $"EVE-{update.LatestVersion}.zip");
        var extractDir = Path.Combine(updateRoot, $"extract-{update.LatestVersion}");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

        using var client = CreateClient();
        progress?.Report(new UpdateDownloadProgress("Downloading update...", 0));
        using (var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(zipPath);
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

        progress?.Report(new UpdateDownloadProgress("Extracting update...", null));
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        TryDelete(zipPath);

        // A zip of the publish folder may contain the files directly, or nested one
        // level under a folder (GitHub-style archives, or a manually zipped folder).
        // Find whichever directory actually contains EVE.exe.
        var sourceRoot = File.Exists(Path.Combine(extractDir, "EVE.exe"))
            ? extractDir
            : Directory.GetDirectories(extractDir).FirstOrDefault(dir => File.Exists(Path.Combine(dir, "EVE.exe")));
        if (sourceRoot is null)
        {
            Directory.Delete(extractDir, true);
            throw new InvalidOperationException("Downloaded update did not contain EVE.exe.");
        }

        progress?.Report(new UpdateDownloadProgress("Restarting...", 1));
        await Task.Delay(100, cancellationToken);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            robocopy '{{Escape(sourceRoot)}}' '{{Escape(installDir)}}' /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS
            Remove-Item -LiteralPath '{{Escape(extractDir)}}' -Recurse -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath '{{Escape(currentExe)}}'
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EVE-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('-')[0], out version!);

    private static async Task<(IReadOnlyList<string> WhatsNew, IReadOnlyList<string> Fixes)> LoadReleaseNotesAsync(HttpClient client, Version latest, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await client.GetStreamAsync(ReleasesUrl, cancellationToken);
            var releases = await JsonSerializer.DeserializeAsync<ReleaseResponse[]>(stream, cancellationToken: cancellationToken) ?? [];
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

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] ReleaseAsset[] Assets,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}
