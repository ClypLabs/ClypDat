using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

/// <summary>
/// Detached signature over the release metadata, verified against a key pinned in the
/// binary.
///
/// Why this exists: the SHA-256 the updater checks the installer against arrives in the
/// same JSON document as the download URL, from one of three separately-controlled hosts
/// (GitHub, GitLab, and the clypdat.xyz mirror with its R2 bucket). Whoever controls a
/// host controls BOTH values, so the digest proves the bytes survived transit - not that
/// they are the bytes ClypDat published. Anyone able to serve that metadata could point
/// the URL at their own installer and publish its digest alongside it.
///
/// The fix is the one the Dev channel already uses: a manifest signed offline with a key
/// that never touches CI or a release host, verified here against the public half
/// compiled into the app. The digest the updater enforces then comes from the signed
/// manifest rather than from the release JSON, so a compromised host can serve whatever
/// it likes and the update still fails.
/// </summary>
public static class ReleaseSigning
{
    public const string ManifestAssetName = "ClypDat-Release.manifest.json";
    public const string SignatureAssetName = "ClypDat-Release.manifest.sig";

    /// <summary>
    /// SubjectPublicKeyInfo of the offline release-signing key, base64.
    ///
    /// EMPTY UNTIL SET UP. Run eng/Generate-ReleaseSigningKey.ps1, then paste the public
    /// half it prints here. While this is empty the updater keeps its previous
    /// digest-only behaviour and logs that signature enforcement is off; the moment it
    /// holds a key, every update must carry a manifest signed by it or be refused.
    /// </summary>
    public const string PublicKeySubjectPublicKeyInfoBase64 = "";

    public static bool IsConfigured => PublicKeySubjectPublicKeyInfoBase64.Length > 0;

    /// <summary>
    /// Verifies the detached signature over the manifest bytes and returns the parsed
    /// manifest. Throws on any failure - a manifest that does not verify is not a
    /// manifest, and callers must not fall back to unsigned metadata.
    /// </summary>
    public static ReleaseManifest Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureBytes)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("No release signing key is pinned in this build.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(signatureBytes).Trim());
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Release manifest signature is not base64.", error);
        }

        // Verify before parsing, so the JSON reader never sees unauthenticated bytes.
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySubjectPublicKeyInfoBase64), out _);
        if (!rsa.VerifyData(manifestBytes.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException("Release manifest signature verification failed.");
        }

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes) ??
            throw new InvalidDataException("Release manifest was empty.");
        if (manifest.Schema != 1) throw new InvalidDataException($"Unsupported release manifest schema {manifest.Schema}.");
        if (string.IsNullOrWhiteSpace(manifest.Tag)) throw new InvalidDataException("Release manifest has no tag.");
        if (manifest.Assets is null || manifest.Assets.Count == 0) throw new InvalidDataException("Release manifest lists no assets.");
        return manifest;
    }

    /// <summary>
    /// Returns the signed SHA-256 for an asset, or null when the manifest does not cover
    /// it. Callers treat null as "refuse the update" rather than falling back.
    /// </summary>
    public static string? FindAssetSha256(ReleaseManifest manifest, string assetName)
    {
        foreach (var asset in manifest.Assets)
        {
            if (string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                var hex = asset.Sha256?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(hex) || hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;
                return hex;
            }
        }

        return null;
    }
}

public sealed record ReleaseManifest
{
    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("tag")] public string Tag { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; init; } = string.Empty;
    [JsonPropertyName("assets")] public IReadOnlyList<ReleaseManifestAsset> Assets { get; init; } = Array.Empty<ReleaseManifestAsset>();
}

public sealed record ReleaseManifestAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
}
