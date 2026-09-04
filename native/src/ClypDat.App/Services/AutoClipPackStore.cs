using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

internal sealed record AutoClipPackFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record AutoClipPackManifest
{
    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("packId")] public string PackId { get; init; } = string.Empty;
    [JsonPropertyName("gameId")] public string GameId { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("minimumAppVersion")] public string MinimumAppVersion { get; init; } = "0.0.0";
    [JsonPropertyName("maximumAppVersion")] public string MaximumAppVersion { get; init; } = "9999.0.0";
    [JsonPropertyName("archiveSize")] public long ArchiveSize { get; init; }
    [JsonPropertyName("archiveSha256")] public string ArchiveSha256 { get; init; } = string.Empty;
    [JsonPropertyName("files")] public IReadOnlyList<AutoClipPackFile> Files { get; init; } = [];
}

internal sealed record AutoClipPackSelection(string PackId, string GameId, string Version, string Hash, string? Directory);

internal sealed class AutoClipPackStore
{
    internal const long MaximumArchiveBytes = 32L * 1024 * 1024;
    internal const long MaximumExtractedBytes = 64L * 1024 * 1024;
    internal const int MaximumFileCount = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public AutoClipPackStore(string? root = null) => _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "AutoClip");

    public AutoClipPackSelection Resolve(string gameId)
    {
        var pointer = ReadPointer(Path.Combine(_root, gameId, "current.json"));
        if (pointer is not null && Directory.Exists(pointer.Directory)) return pointer;
        return BuiltIn(gameId);
    }

    public AutoClipPackSelection InstallSigned(
        string archivePath,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        Version appVersion)
    {
        ReleaseSigning.VerifyDetached(manifestBytes, signatureBytes, "Detector pack manifest");
        var manifest = JsonSerializer.Deserialize<AutoClipPackManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("Detector pack manifest was empty.");
        ValidateManifest(manifest, appVersion);
        var archive = new FileInfo(archivePath);
        if (!archive.Exists || archive.Length != manifest.ArchiveSize || archive.Length > MaximumArchiveBytes)
            throw new InvalidDataException("Detector pack archive size is invalid.");
        using (var stream = archive.OpenRead())
            RequireHash(stream, manifest.ArchiveSha256, "Detector pack archive");

        var gameRoot = Path.Combine(_root, manifest.GameId);
        var final = Path.Combine(gameRoot, "packs", manifest.Version + "-" + manifest.ArchiveSha256[..12].ToLowerInvariant());
        var staging = Path.Combine(gameRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            VerifyAndExtractArchive(archivePath, staging, manifest.Files);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (!Directory.Exists(final)) Directory.Move(staging, final);
            else Directory.Delete(staging, true);
            var selection = new AutoClipPackSelection(manifest.PackId, manifest.GameId, manifest.Version,
                manifest.ArchiveSha256.ToLowerInvariant(), final);
            Activate(gameRoot, selection);
            return selection;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    public void Quarantine(AutoClipPackSelection selection)
    {
        if (selection.Directory is null) return;
        var gameRoot = Path.Combine(_root, selection.GameId);
        var quarantine = Path.Combine(gameRoot, "quarantine");
        Directory.CreateDirectory(quarantine);
        WriteAtomic(Path.Combine(quarantine, selection.Version + ".json"), selection);
        var previous = ReadPointer(Path.Combine(gameRoot, "previous.json"));
        if (previous is not null) WriteAtomic(Path.Combine(gameRoot, "current.json"), previous);
        else if (File.Exists(Path.Combine(gameRoot, "current.json"))) File.Delete(Path.Combine(gameRoot, "current.json"));
    }

    private static void ValidateManifest(AutoClipPackManifest manifest, Version appVersion)
    {
        if (manifest.Schema != 1) throw new InvalidDataException($"Unsupported detector pack schema {manifest.Schema}.");
        RequireIdentifier(manifest.PackId, "pack ID"); RequireIdentifier(manifest.GameId, "game ID");
        if (!Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Detector pack version is invalid.");
        if (!Version.TryParse(manifest.MinimumAppVersion, out var minimum) || !Version.TryParse(manifest.MaximumAppVersion, out var maximum)
            || appVersion < minimum || appVersion > maximum) throw new InvalidDataException("Detector pack is incompatible with this app version.");
        if (manifest.ArchiveSize is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("Detector pack archive size is invalid.");
        RequireSha256(manifest.ArchiveSha256);
        if (manifest.Files.Count is <= 0 or > MaximumFileCount) throw new InvalidDataException("Detector pack file count is invalid.");
        if (manifest.Files.Sum(file => file.Size) > MaximumExtractedBytes) throw new InvalidDataException("Detector pack extracted size is too large.");
    }

    internal static void VerifyAndExtractArchive(string archivePath, string staging, IReadOnlyList<AutoClipPackFile> expectedFiles)
    {
        var expected = expectedFiles.ToDictionary(file => NormalizeEntry(file.Path), StringComparer.OrdinalIgnoreCase);
        if (expected.Count != expectedFiles.Count) throw new InvalidDataException("Detector pack has duplicate paths.");
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length != expected.Count || files.Length > MaximumFileCount) throw new InvalidDataException("Detector pack archive contains unexpected files.");
        long extracted = 0;
        foreach (var entry in files)
        {
            var path = NormalizeEntry(entry.FullName);
            if (!expected.TryGetValue(path, out var expectedFile) || entry.Length != expectedFile.Size)
                throw new InvalidDataException($"Detector pack file '{path}' is unexpected or has the wrong size.");
            extracted = checked(extracted + entry.Length);
            if (extracted > MaximumExtractedBytes) throw new InvalidDataException("Detector pack extracted size is too large.");
            var destination = Path.GetFullPath(Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Detector pack path escapes staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = entry.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            RequireHash(memory, expectedFile.Sha256, $"Detector pack file '{path}'");
            File.WriteAllBytes(destination, memory.ToArray());
        }
    }

    private static string NormalizeEntry(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Detector pack contains an unsafe path.");
        var extension = Path.GetExtension(normalized);
        if (extension is not (".json" or ".png" or ".onnx" or ".bin"))
            throw new InvalidDataException("Detector packs may contain declarative assets only.");
        return normalized;
    }

    private static void RequireIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new InvalidDataException($"Detector pack {label} is invalid.");
    }

    private static void RequireSha256(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Detector pack SHA-256 is invalid.");
    }

    private static void RequireHash(Stream stream, string expected, string label)
    {
        RequireSha256(expected); stream.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new InvalidDataException($"{label} SHA-256 does not match.");
    }

    private static AutoClipPackSelection BuiltIn(string gameId) => gameId switch
    {
        "helldivers2" => new("clypdat.helldivers2", gameId, "0.1.0", "builtin", null),
        _ => throw new InvalidOperationException($"No detector pack is installed for '{gameId}'.")
    };

    private void Activate(string gameRoot, AutoClipPackSelection selection)
    {
        Directory.CreateDirectory(gameRoot);
        var currentPath = Path.Combine(gameRoot, "current.json");
        var current = ReadPointer(currentPath);
        if (current is not null && current != selection) WriteAtomic(Path.Combine(gameRoot, "previous.json"), current);
        WriteAtomic(currentPath, selection);
    }

    private static AutoClipPackSelection? ReadPointer(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AutoClipPackSelection>(File.ReadAllBytes(path), JsonOptions) : null; }
        catch { return null; }
    }

    private static void WriteAtomic(string path, AutoClipPackSelection selection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(selection, JsonOptions));
        File.Move(temporary, path, true);
    }
}
