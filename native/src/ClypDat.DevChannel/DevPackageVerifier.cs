using System.IO.Compression;
using System.Security.Cryptography;

namespace ClypDat.DevChannel;

public static class DevPackageVerifier
{
    public static DevBuildManifest VerifyManifest(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureBytes)
    {
        // Verify first, parse second. Deserializing unauthenticated bytes before the
        // signature check ran the JSON parser over attacker-supplied input for no
        // reason; the depth limit contained it, but the ordering was backwards.
        byte[] signature;
        try { signature = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(signatureBytes).Trim()); }
        catch (FormatException error) { throw new InvalidDataException("Dev manifest signature is not base64.", error); }

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(DevChannelConstants.PublicKeySubjectPublicKeyInfoBase64), out _);
        if (!rsa.VerifyData(manifestBytes.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Dev manifest signature verification failed.");

        return DevBuildManifest.Parse(manifestBytes);
    }

    public static void VerifyArchive(Stream archive, DevBuildManifest manifest)
    {
        if (!archive.CanSeek) throw new ArgumentException("The archive stream must be seekable.", nameof(archive));
        if (archive.Length != manifest.ArchiveSize) throw new InvalidDataException("Dev archive size does not match its manifest.");
        archive.Position = 0;
        var hash = SHA256.HashData(archive);
        var actualHash = Convert.ToHexString(hash);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(manifest.ArchiveSha256)))
            throw new CryptographicException($"Dev archive SHA-256 mismatch: expected {manifest.ArchiveSha256}, got {actualHash}.");
        archive.Position = 0;
    }

    public static void ExtractArchive(Stream archive, string destination, DevBuildManifest manifest)
    {
        VerifyArchive(archive, manifest);
        if (Directory.Exists(destination)) throw new IOException("The Dev staging directory already exists.");
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count == 0 || zip.Entries.Count > DevChannelConstants.MaximumArchiveEntries)
                throw new InvalidDataException("Dev archive entry count is invalid.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temporaryFullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporary)) + Path.DirectorySeparatorChar;
            long totalUncompressed = 0;
            foreach (var entry in zip.Entries)
            {
                var relative = ValidateEntry(entry);
                if (!seen.Add(relative)) throw new InvalidDataException("Dev archive contains duplicate entries.");
                // entry.Length is the size DECLARED in the central directory, which the
                // archive author controls; the copy below writes however many bytes the
                // stream actually produces. Checking the declared value alone let a
                // crafted archive understate its size and expand without bound.
                if (checked(totalUncompressed + entry.Length) > ExpansionBudget)
                    throw new InvalidDataException("Dev archive expands beyond the allowed limit.");
                if (relative.Length == 0) continue;
                var outputPath = Path.Combine(temporary, relative.Replace('/', Path.DirectorySeparatorChar));

                // The staging directory must still contain the composed path. Belt and
                // braces over ValidateEntry, which rejects traversal on the way in.
                if (!Path.GetFullPath(outputPath).StartsWith(temporaryFullRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Dev archive entry escapes the staging directory.");

                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(outputPath); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = entry.Open();
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                totalUncompressed += CopyBounded(input, output, ExpansionBudget - totalUncompressed);
            }

            if (!File.Exists(Path.Combine(temporary, "ClypDat.exe")))
                throw new InvalidDataException("Dev archive does not contain ClypDat.exe.");
            Directory.Move(temporary, destination);
        }
        catch
        {
            TryDeleteDirectory(temporary);
            throw;
        }
    }

    private static string ValidateEntry(ZipArchiveEntry entry)
    {
        if (entry.Length < 0) throw new InvalidDataException("Dev archive contains an invalid entry.");
        var raw = entry.FullName.Replace('\\', '/');
        // IsPathFullyQualified("C:evil.dll") is FALSE - it is drive-RELATIVE, not
        // fully qualified - while IsPathRooted is true, and Path.Combine discards its
        // first argument for any rooted second one. That put the entry on drive C:'s
        // working directory, which DevLauncher sets to the version folder, i.e. right
        // beside ClypDat.exe.
        if (raw.StartsWith('/') || raw.StartsWith("//") ||
            Path.IsPathFullyQualified(raw) || Path.IsPathRooted(raw) ||
            (raw.Length >= 2 && raw[1] == ':') ||
            raw.Any(char.IsControl))
            throw new InvalidDataException("Dev archive contains an absolute or invalid path.");
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new InvalidDataException("Dev archive contains traversal.");
        if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            throw new InvalidDataException("Dev archive contains a symbolic link.");
        return string.Join('/', parts);
    }

    private const long ExpansionBudget = DevChannelConstants.MaximumArchiveBytes * 2;

    // Copies at most 'budget' bytes, throwing if the stream has more to give. This is
    // the check that actually bounds expansion, since it counts real bytes.
    private static long CopyBounded(Stream input, Stream output, long budget)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > budget) throw new InvalidDataException("Dev archive expands beyond the allowed limit.");
            output.Write(buffer, 0, read);
        }

        return copied;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
