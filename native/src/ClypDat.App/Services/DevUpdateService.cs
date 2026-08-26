using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClypDat.DevChannel;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public static class DevUpdateService
{
    private const string Owner = "ClypLabs";
    private const string Repository = "ClypDat";

    // Derived from the shared constant so the tag can never drift out of sync again.
    private static readonly string ReleaseApiUrl =
        $"https://api.github.com/repos/{Owner}/{Repository}/releases/tags/{DevChannelConstants.ReleaseTag}";

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
        // The archive download enforced host + path prefix + size; these two did not,
        // so the manifest and its signature - the inputs the whole dev-channel trust
        // model rests on - were fetched from whatever URL the release metadata named,
        // with no size bound.
        var manifestBytes = await GetTrustedAssetAsync(client, manifestAsset.DownloadUrl, MaximumManifestBytes, cancellationToken);
        var signatureBytes = await GetTrustedAssetAsync(client, signatureAsset.DownloadUrl, MaximumSignatureBytes, cancellationToken);
        var manifest = DevPackageVerifier.VerifyManifest(manifestBytes, signatureBytes);
        if (manifest.BuildIdSource != $"{manifest.ClypDatCommit}-{manifest.AvaloniaCommit[..7]}")
            throw new InvalidDataException("Dev manifest build identity is inconsistent.");

        var root = AppDataPaths.Root;
        var versionsRoot = DevChannelPaths.VersionsRootFor(root);
        var statePath = DevChannelPaths.StatePathFor(root);
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

    // A manifest is a few hundred bytes and a signature is a base64 RSA-3072 block;
    // these caps are orders of magnitude above either.
    private const long MaximumManifestBytes = 64 * 1024;
    private const long MaximumSignatureBytes = 8 * 1024;

    private static Uri EnsureTrustedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Dev asset URL is not trusted.");
        return uri;
    }

    private static async Task<byte[]> GetTrustedAssetAsync(HttpClient client, string url, long maximumBytes, CancellationToken cancellationToken)
    {
        var uri = EnsureTrustedAssetUrl(url);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException($"Dev asset {uri.AbsolutePath} exceeds {maximumBytes} bytes.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException($"Dev asset {uri.AbsolutePath} exceeds {maximumBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task DownloadAsync(HttpClient client, string url, string path, long expectedSize, CancellationToken cancellationToken)
    {
        var uri = EnsureTrustedAssetUrl(url);
        var partial = path + ".partial";
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > DevChannelConstants.MaximumArchiveBytes)
                throw new InvalidDataException("Dev archive is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            long total = 0;
            // The stream must be scoped tighter than the method: it holds the partial file
            // with FileShare.None, so File.Move below cannot run while it is still open.
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + read);
                    if (total > expectedSize || total > DevChannelConstants.MaximumArchiveBytes) throw new InvalidDataException("Dev archive exceeded its manifest size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
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
