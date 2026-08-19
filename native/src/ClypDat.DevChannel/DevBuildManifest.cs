using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.DevChannel;

public sealed record DevBuildManifest(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("clypDatCommit")] string ClypDatCommit,
    [property: JsonPropertyName("avaloniaCommit")] string AvaloniaCommit,
    [property: JsonPropertyName("avaloniaPackageVersion")] string AvaloniaPackageVersion,
    [property: JsonPropertyName("buildIdSource")] string BuildIdSource,
    [property: JsonPropertyName("buildNumber")] long BuildNumber,
    [property: JsonPropertyName("archiveSize")] long ArchiveSize,
    [property: JsonPropertyName("archiveSha256")] string ArchiveSha256,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
{
    public static DevBuildManifest Parse(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<DevBuildManifest>(bytes) ??
            throw new InvalidDataException("Dev manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(DevBuildManifest manifest)
    {
        if (manifest.Schema != 1) throw new InvalidDataException("Unsupported Dev manifest schema.");
        RequireSafeToken(manifest.BuildId, nameof(manifest.BuildId), 128);
        RequireSha256(manifest.ArchiveSha256, nameof(manifest.ArchiveSha256));
        if (manifest.ArchiveSize <= 0 || manifest.ArchiveSize > DevChannelConstants.MaximumArchiveBytes)
            throw new InvalidDataException("Dev archive size is outside the allowed range.");
        if (!IsCommit(manifest.ClypDatCommit) || !IsCommit(manifest.AvaloniaCommit))
            throw new InvalidDataException("Dev manifest contains an invalid commit.");
        if (!manifest.AvaloniaPackageVersion.StartsWith("12.2.1000-clypdat.", StringComparison.Ordinal))
            throw new InvalidDataException("Dev manifest contains an invalid Avalonia package version.");
    }

    private static bool IsCommit(string value) =>
        value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);

    private static void RequireSha256(string value, string field) =>
        RequireSafeToken(value, field, 64, expectedLength: 64, hexOnly: true);

    private static void RequireSafeToken(string value, string field, int maximum, int? expectedLength = null, bool hexOnly = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || (expectedLength is not null && value.Length != expectedLength) ||
            (hexOnly && !value.All(Uri.IsHexDigit)) || (!hexOnly && value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))))
            throw new InvalidDataException($"Dev manifest field '{field}' is invalid.");
    }
}
