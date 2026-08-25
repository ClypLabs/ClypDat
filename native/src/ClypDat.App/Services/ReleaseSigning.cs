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
    /// SubjectPublicKeyInfo of every trusted release-signing key, base64.
    ///
    /// EMPTY UNTIL SET UP. Run eng/Generate-ReleaseSigningKey.ps1 and paste the public
    /// half it prints here. While this list is empty the updater keeps its previous
    /// digest-only behaviour and logs that enforcement is off; as soon as it holds a
    /// key, every update must carry a manifest signed by one of these or be refused.
    ///
    /// A LIST rather than a single key, for two reasons:
    ///
    ///   Multiple signers. If more than one person cuts releases, they each keep their
    ///   own private key instead of sharing one. A leak is then contained to that
    ///   signer, and it stays answerable who signed a given release - a shared key makes
    ///   that unanswerable. NEVER hand a private key to a second person; add their
    ///   public key here instead.
    ///
    ///   Rotation. The trusted set lives in the binary, so replacing a key means
    ///   shipping a build that trusts the new one. Publishing the replacement's public
    ///   key alongside the current one - so both are accepted for a release or two -
    ///   lets a key be retired without a flag day, and lets a compromised key be dropped
    ///   without stranding users on a build that trusts nothing else.
    ///
    /// Order is irrelevant; a signature from any entry is accepted.
    /// </summary>
    public static readonly IReadOnlyList<PinnedReleaseKey> PinnedPublicKeys = new PinnedReleaseKey[]
    {
        new PinnedReleaseKey("arashii", "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA1REPK3NqTAR33oWpngYIh4Wmvp5sgDFRAn2YWiqwV56dOeAitGmXDfvE0NtVm7igLiqGiNSBW/vrH8GErllwIUcUpbTddIPG9gVf9H0QsVXzudeK0/REsc++3j+kq3FASujX0+cAtiB0yatGyMhiq2c0HSICjqOA5YhrzNMgCXNbdlEHNK3zPdhmQIoUEwxTDv6VFzfGSzTX7Xq43FF9Jsv0Yc0Cvm54KYthDnxl3zH4JyTI6Za1PSQivJNFfP/7b3UriccCShwyrzIvu6sW2n37GltJFSzH1EXzWrC8gXagcqk8Ym9CASV2p78oLo95k2oT0+LVfPfIfV60tWFbPCyqLFd4RzoryWaQaQtEWPmSg798VDNR7qR8evf4/W2ettUs+z55QF10TqGWozzCNJHLRKjhd+3pixT8kkiXeUYGUk7xdBsZOBWdRBT1GBFJqnpDHu/1oEjzgnU6NyyD70fclq+W5t8um1mAJt2e5xZXISCGsaMrvJSnTHMnsJeBAgMBAAE="),
    };

    public static bool IsConfigured => PinnedPublicKeys.Count > 0;

    /// <summary>
    /// Short, stable identifier for a key: the first 16 hex characters of the SHA-256 of
    /// its SubjectPublicKeyInfo. Logged so it is visible which key accepted a release.
    /// </summary>
    public static string Fingerprint(string subjectPublicKeyInfoBase64)
    {
        try
        {
            var hash = SHA256.HashData(Convert.FromBase64String(subjectPublicKeyInfoBase64));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// Verifies the detached signature over the manifest bytes and returns the parsed
    /// manifest. Throws on any failure - a manifest that does not verify is not a
    /// manifest, and callers must not fall back to unsigned metadata.
    /// </summary>
    public static ReleaseManifest Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureBytes) =>
        Verify(manifestBytes, signatureBytes, out _);

    /// <summary>
    /// As <see cref="Verify(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>, additionally
    /// reporting which pinned key accepted the signature.
    /// </summary>
    public static ReleaseManifest Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureBytes, out PinnedReleaseKey acceptedBy)
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
        // Every pinned key is tried; a signature from any of them is accepted. A key that
        // will not even import is skipped rather than failing the whole check, so one
        // malformed entry cannot strand users on a build that trusts nothing.
        var manifestArray = manifestBytes.ToArray();
        PinnedReleaseKey? accepted = null;
        foreach (var candidate in PinnedPublicKeys)
        {
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(candidate.SubjectPublicKeyInfoBase64), out _);
            }
            catch (Exception error)
            {
                AppLog.Error($"Pinned release key '{candidate.Label}' could not be imported; skipping it.", error);
                continue;
            }

            if (rsa.VerifyData(manifestArray, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                accepted = candidate;
                break;
            }
        }

        if (accepted is null)
        {
            throw new CryptographicException(
                $"Release manifest signature did not verify against any of the {PinnedPublicKeys.Count} pinned release key(s).");
        }

        acceptedBy = accepted;

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

/// <summary>A release-signing public key trusted by this build.</summary>
/// <param name="Label">Human-readable name for logs - who or what holds the private half.</param>
/// <param name="SubjectPublicKeyInfoBase64">Base64 SubjectPublicKeyInfo of the public key.</param>
public sealed record PinnedReleaseKey(string Label, string SubjectPublicKeyInfoBase64)
{
    public string Fingerprint => ReleaseSigning.Fingerprint(SubjectPublicKeyInfoBase64);
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
