using System.Buffers.Binary;
using System.Net;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Public cosmetic artwork only. A replay supplies the worn outfit ID; no
// account, match, replay path or authentication data leaves this machine.
internal sealed class FortniteOutfitPortraitCache
{
    internal const int MaximumImageBytes = 1024 * 1024;
    private const int MaximumDimension = 512;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    internal static FortniteOutfitPortraitCache Default { get; } = new(AppDataPaths.Root);
    private readonly string _ownerRoot;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime> _failedUntil = new(StringComparer.Ordinal);
    internal string CacheRoot { get; }

    internal FortniteOutfitPortraitCache(string ownerRoot, HttpClient? http = null)
    {
        _ownerRoot = ResolveOwnedRoot(ownerRoot);
        CacheRoot = Path.Combine(_ownerRoot, "cosmetics");
        _http = http ?? Http;
    }

    // The product's configured AppData location is trusted and may itself
    // have been moved with a junction, including one in an ancestor folder.
    // Canonicalise that location once; links planted beneath it remain
    // untrusted and are still rejected by the ordinary cache path guard.
    private static string ResolveOwnedRoot(string ownerRoot)
    {
        var original = Path.GetFullPath(ownerRoot);
        var resolved = original;
        try
        {
            for (var attempt = 0; attempt < 64; ++attempt)
            {
                var drive = Path.GetPathRoot(resolved)!;
                var components = resolved[drive.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                var current = drive;
                var followed = false;
                foreach (var component in components)
                {
                    current = Path.Combine(current, component);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(current); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                    var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null) return original;
                    var remainder = Path.GetRelativePath(current, resolved);
                    resolved = Path.GetFullPath(remainder == "." ? target.FullName : Path.Combine(target.FullName, remainder));
                    followed = true;
                    break;
                }
                if (!followed) return resolved;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        // An unresolved or cyclic root remains subject to the strict guard,
        // which refuses the request without affecting the rest of Stats.
        return original;
    }

    internal static string? NormaliseId(string? outfitId)
    {
        if (outfitId is null || outfitId.Length is < 5 or > 128 ||
            !outfitId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) return null;
        if (!(outfitId.Length > 10 && outfitId.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)) &&
            !outfitId.StartsWith("CID_", StringComparison.OrdinalIgnoreCase)) return null;
        return outfitId.ToLowerInvariant();
    }

    internal static Uri? ImageUri(string? outfitId) => NormaliseId(outfitId) is { } id
        ? new Uri($"https://fortnite-api.com/images/cosmetics/br/{id}/smallicon.png") : null;

    internal static bool IsBoundedPng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length is < 33 or > MaximumImageBytes || !bytes[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        return width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension;
    }

    internal async Task<byte[]?> GetBytesAsync(string? outfitId, CancellationToken cancellationToken)
    {
        if (NormaliseId(outfitId) is not { } id) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(CacheRoot, id + ".png");
            if (!LibraryPathGuard.IsWithin(_ownerRoot, path)) return null;
            if (File.Exists(path))
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, useAsync: true);
                var cached = await ReadBoundedAsync(file, cancellationToken).ConfigureAwait(false);
                if (cached is not null && IsBoundedPng(cached)) return cached;
            }
            if (_failedUntil.TryGetValue(id, out var until) && until > DateTime.UtcNow) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(ImageUri(id), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumImageBytes ||
                response.Content.Headers.ContentType?.MediaType is not (null or "image/png")) return Failed(id);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(body, deadline.Token).ConfigureAwait(false);
            if (bytes is null || !IsBoundedPng(bytes)) return Failed(id);
            cancellationToken.ThrowIfCancellationRequested();
            if (!LibraryPathGuard.IsWithin(_ownerRoot, path)) return null;
            Directory.CreateDirectory(CacheRoot);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!LibraryPathGuard.IsWithin(_ownerRoot, path)) return null;
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _failedUntil.Remove(id);
            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        { return Failed(id); }
        finally { _gate.Release(); }
    }

    private byte[]? Failed(string id)
    {
        if (_failedUntil.Count >= 64) _failedUntil.Clear();
        _failedUntil[id] = DateTime.UtcNow.AddMinutes(2);
        return null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > MaximumImageBytes) return null;
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumImageBytes) return null;
            output.Write(buffer, 0, read);
        }
    }
}
