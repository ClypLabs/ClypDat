using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Eve.App.Services;

public sealed record MedalClipRecord(
    string VideoPath,
    string? ThumbnailPath,
    string GameFolderName,
    DateTime CreatedAtUtc,
    string? Title);

// Medal stores its local clip catalog in %AppData%\Medal\medal-<accountId>.db (one
// per account ever signed into, plus medal-guest.db) - a SQLite database with a
// "contents" table holding the plain file paths directly as columns, but the clip
// title lives inside an opaque "metadata" BLOB in a proprietary binary format
// (not JSON, not standard BSON/MessagePack - empirically reverse-engineered by
// inspecting real bytes, see TryExtractTitle). There is no public schema for this,
// so title extraction is best-effort: if it fails for a given clip, the clip is
// still importable, just without a nice title.
public static class MedalImportService
{
    public static IReadOnlyList<MedalClipRecord> ScanForClips()
    {
        var medalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medal");
        if (!Directory.Exists(medalRoot)) return Array.Empty<MedalClipRecord>();

        var results = new List<MedalClipRecord>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dbPath in Directory.EnumerateFiles(medalRoot, "medal-*.db"))
        {
            try
            {
                ReadDatabase(dbPath, results, seenPaths);
            }
            catch (Exception error)
            {
                AppLog.Error($"Medal import: failed reading {dbPath}", error);
            }
        }

        return DedupeBySizeAndGame(results);
    }

    // Medal itself sometimes catalogs the same clip twice under different video_path
    // entries (reported duplicate-clip bug) - path-string dedupe above won't catch
    // that since the paths differ. Same file size within the same game folder is
    // treated as the same clip; first occurrence wins.
    private static List<MedalClipRecord> DedupeBySizeAndGame(List<MedalClipRecord> results)
    {
        var seen = new HashSet<(long Length, string GameFolder)>();
        var deduped = new List<MedalClipRecord>();
        foreach (var record in results)
        {
            long length;
            try { length = new FileInfo(record.VideoPath).Length; }
            catch { deduped.Add(record); continue; }

            if (seen.Add((length, record.GameFolderName)))
            {
                deduped.Add(record);
            }
        }

        return deduped;
    }

    private static void ReadDatabase(string dbPath, List<MedalClipRecord> results, HashSet<string> seenPaths)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at, video_path, thumbnail_path, metadata FROM contents WHERE video_path IS NOT NULL";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var videoPath = reader.GetString(1);
            if (!seenPaths.Add(videoPath)) continue;
            if (!File.Exists(videoPath)) continue;

            var thumbnailPath = reader.IsDBNull(2) ? null : reader.GetString(2);
            var createdAtUnix = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
            var createdAtUtc = createdAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(createdAtUnix).UtcDateTime : File.GetCreationTimeUtc(videoPath);
            var metadata = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
            var title = metadata is null ? null : TryExtractTitle(metadata);
            var gameFolder = Path.GetFileName(Path.GetDirectoryName(videoPath)) ?? "Medal";

            results.Add(new MedalClipRecord(videoPath, thumbnailPath, gameFolder, createdAtUtc, title));
        }
    }

    // Finds the literal ASCII "title" key marker followed by Medal's observed
    // length-prefixed-string encoding (0xC7 + 1-byte length + that many UTF-8
    // bytes) and decodes it. Confirmed against a real Medal record where this
    // produced the exact title Medal's own UI shows ("🔥4K - Inferno"). Returns
    // null rather than throwing if the pattern isn't found or doesn't decode -
    // the caller falls back to a generic name.
    internal static string? TryExtractTitle(byte[] metadata)
    {
        var key = "title"u8.ToArray();
        for (var i = 0; i <= metadata.Length - key.Length - 2; i++)
        {
            var isMatch = true;
            for (var k = 0; k < key.Length; k++)
            {
                if (metadata[i + k] != key[k]) { isMatch = false; break; }
            }

            if (!isMatch) continue;

            var valuePos = i + key.Length;
            if (metadata[valuePos] != 0xC7) continue;

            var length = metadata[valuePos + 1];
            var stringStart = valuePos + 2;
            if (stringStart + length > metadata.Length) continue;

            try
            {
                var value = Encoding.UTF8.GetString(metadata, stringStart, length);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // Not actually a title match at this offset - keep scanning.
            }
        }

        return null;
    }

    public static string StripEmoji(string text)
    {
        // Strips emoji/pictographs and variation selectors while leaving normal
        // text (including non-Latin scripts) untouched.
        var stripped = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[☀-➿]|️", string.Empty);
        return Regex.Replace(stripped, @"\s{2,}", " ").Trim();
    }
}
