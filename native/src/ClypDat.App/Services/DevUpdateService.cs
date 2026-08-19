using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClypDat.DevChannel;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public static class DevUpdateService
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/ClypDat/ClypDat/releases/tags/dev-channel";
    private const string Owner = "ClypDat";
    private const string Repository = "ClypDat";

    public static void StartBackgroundCheck()
    {
        if (!DevChannelMode.Enabled) return;
        _ = Task.Run(async () =>
        {
            try { await StageLatestAsync(); }
            catch (Exception error) { AppLog.Error("Dev update staging failed", error); }
        });
    }

    internal static async Task StageLatestAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-Dev");
        var release = await client.GetFromJsonAsync<ReleaseResponse>(ReleaseApiUrl, cancellationToken) ??
            throw new InvalidDataException("Dev release metadata was empty.");
        if (!string.Equals(release.TagName, DevChannelConstants.ReleaseTag, StringComparison.Ordinal) || release.Draft)
            throw new InvalidDataException("Dev release metadata was not the signed dev channel.");
        var manifestAsset = Find(release, DevChannelConstants.ManifestAssetName);
        var signatureAsset = Find(release, DevChannelConstants.SignatureAssetName);
        var archiveAsset = Find(release, DevChannelConstants.ArchiveAssetName);
        var manifestBytes = await client.GetByteArrayAsync(manifestAsset.DownloadUrl, cancellationToken);
        var signatureBytes = await client.GetByteArrayAsync(signatureAsset.DownloadUrl, cancellationToken);
        var manifest = DevPackageVerifier.VerifyManifest(manifestBytes, signatureBytes);
        if (manifest.BuildIdSource != $"{manifest.ClypDatCommit}-{manifest.AvaloniaCommit[..7]}")
            throw new InvalidDataException("Dev manifest build identity is inconsistent.");

        var root = AppDataPaths.Root;
        var versionsRoot = Path.Combine(root, "versions");
        var statePath = Path.Combine(root, "state.json");
        var current = DevInstallStateStore.Load(statePath);
        if (string.Equals(current.CurrentBuildId, manifest.BuildId, StringComparison.Ordinal) ||
            string.Equals(current.PendingBuildId, manifest.BuildId, StringComparison.Ordinal)) return;
        Directory.CreateDirectory(versionsRoot);
        var archivePath = Path.Combine(root, "staging", manifest.BuildId + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        await DownloadAsync(client, archiveAsset.DownloadUrl, archivePath, manifest.ArchiveSize, cancellationToken);
        await using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var destination = Path.Combine(versionsRoot, manifest.BuildId);
            DevPackageVerifier.ExtractArchive(archive, destination, manifest);
        }
        File.Delete(archivePath);
        DevInstallStateStore.SaveAtomic(statePath, current with { PendingBuildId = manifest.BuildId });
    }

    private static async Task DownloadAsync(HttpClient client, string url, string path, long expectedSize, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Dev archive URL is not trusted.");
        var partial = path + ".partial";
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > DevChannelConstants.MaximumArchiveBytes)
                throw new InvalidDataException("Dev archive is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total = checked(total + read);
                if (total > expectedSize || total > DevChannelConstants.MaximumArchiveBytes) throw new InvalidDataException("Dev archive exceeded its manifest size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total != expectedSize) throw new InvalidDataException("Dev archive download was incomplete.");
            File.Move(partial, path, overwrite: true);
        }
        catch { try { File.Delete(partial); } catch { } throw; }
    }

    private static AssetResponse Find(ReleaseResponse release, string name) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal)) ??
        throw new InvalidDataException($"Dev release is missing {name}.");

    private sealed record ReleaseResponse([property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("draft")] bool Draft,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] AssetResponse[] Assets);
    private sealed record AssetResponse([property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string DownloadUrl);
}
