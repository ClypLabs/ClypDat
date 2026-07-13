using System.Globalization;
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
        var results = new List<MedalClipRecord>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(medalRoot))
        {
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
        }

        ScanClipsFolderFallback(results, seenPaths);

        return DedupeBySizeAndGame(results);
    }

    // Medal's own catalog (medal-*.db) can go missing or get corrupted - a
    // reported real case: a user's entire database was lost, and every clip
    // still sitting in Medal's own clips folder on disk (confirmed present in
    // File Explorer) stopped showing up in EVE's import list, because the DB
    // pass above is the only thing that ever looked for clips. This scans
    // Medal's actual clips folder directly for video files the DB pass didn't
    // already find, so losing the DB only costs the nice DB-sourced titles,
    // not the clips themselves. Medal's default save location; if a user
    // customized it in Medal's own settings this won't see those, but there's
    // no DB left to read a custom path from either in the scenario this
    // exists for.
    private static void ScanClipsFolderFallback(List<MedalClipRecord> results, HashSet<string> seenPaths)
    {
        var clipsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Medal");
        if (!Directory.Exists(clipsRoot)) return;

        var videoExtensions = new[] { ".mp4", ".mov", ".mkv" };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(clipsRoot, "*.*", SearchOption.AllDirectories);
        }
        catch (Exception error)
        {
            AppLog.Error($"Medal import: failed scanning clips folder {clipsRoot}", error);
            return;
        }

        foreach (var videoPath in files)
        {
            if (!videoExtensions.Contains(Path.GetExtension(videoPath), StringComparer.OrdinalIgnoreCase)) continue;
            if (!seenPaths.Add(videoPath)) continue;

            var gameFolder = Path.GetFileName(Path.GetDirectoryName(videoPath)) ?? "Medal";
            var fileStem = Path.GetFileNameWithoutExtension(videoPath);

            DateTime createdAtUtc;
            string? title;
            if (TryParseRawFilenameTimestamp(fileStem, out var embeddedLocalTimestamp))
            {
                // The embedded timestamp is the real recording moment Medal itself
                // encoded - more trustworthy than the file's own Windows metadata,
                // which can drift if the file was ever copied or moved.
                createdAtUtc = DateTime.SpecifyKind(embeddedLocalTimestamp, DateTimeKind.Local).ToUniversalTime();
                title = IsRawFilenameJustGameAndTimestamp(fileStem, gameFolder) ? gameFolder : null;
            }
            else
            {
                // No DB row for this file (missing/corrupt DB, or Medal just
                // never cataloged it) - the file's own Windows timestamp is the
                // only real signal available for when it was actually recorded.
                try { createdAtUtc = File.GetCreationTimeUtc(videoPath); }
                catch { createdAtUtc = DateTime.UtcNow; }
                title = TryParseTrimTitle(fileStem, gameFolder);
            }

            results.Add(new MedalClipRecord(videoPath, null, gameFolder, createdAtUtc, title));
        }
    }

    // Medal names a trimmed export "MedalTV<GameTitle><yyyy-MM-dd HH-mm-ss>-trim-<n>"
    // with no DB metadata blob for the trimmed file itself in the cases seen
    // (the trim is a separate export, not the original cataloged clip) - the
    // raw filename stem is genuinely ugly as a title, so this swaps in the
    // already-reliable game-folder name instead. Not the same as recovering
    // Medal's own custom title (that data doesn't appear to exist for a trim
    // export to recover from), but a real improvement over the raw filename.
    private static readonly Regex TrimFilenamePattern = new(
        @"^MedalTV.*?\d{4}-\d{2}-\d{2}[ _]\d{2}-\d{2}-\d{2}.*-trim-\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string? TryParseTrimTitle(string fileNameWithoutExtension, string gameFolder)
    {
        if (!TrimFilenamePattern.IsMatch(fileNameWithoutExtension)) return null;
        return string.IsNullOrWhiteSpace(gameFolder) ? "Trimmed Clip" : $"{gameFolder} (Trimmed)";
    }

    // Medal's default (non-trimmed) export filename is "MedalTV<GameTitle>
    // <yyyyMMddHHmmss>" with no separators at all - e.g.
    // "MedalTVCounterStrike220250626224059" - which is both a genuinely
    // unreadable title when there's no DB title to use instead, and (since it's
    // the exact moment Medal itself recorded, unlike the file's own Windows
    // timestamp which can drift if the file was ever copied/moved) a better
    // source for the clip's date than anything else available in that case.
    private static readonly Regex RawFilenameTimestampPattern = new(
        @"^MedalTV(?<game>.*?)(?<ts>\d{14})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool TryParseRawFilenameTimestamp(string fileNameWithoutExtension, out DateTime localTimestamp)
    {
        var match = RawFilenameTimestampPattern.Match(fileNameWithoutExtension);
        if (match.Success && DateTime.TryParseExact(
                match.Groups["ts"].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out localTimestamp))
        {
            return true;
        }

        localTimestamp = default;
        return false;
    }

    // Only safe to swap in the plain game-folder name when the filename's
    // middle segment IS just the game's own name with the spaces/punctuation
    // stripped (Medal's default, uninformative export name). If it's anything
    // else - e.g. a highlight-style label - that's actual signal (matches
    // EVE's own auto-clip titles like "4K - Mirage"), not junk, and must be
    // left alone rather than flattened to just the game name.
    internal static bool IsRawFilenameJustGameAndTimestamp(string fileNameWithoutExtension, string gameFolder)
    {
        var match = RawFilenameTimestampPattern.Match(fileNameWithoutExtension);
        if (!match.Success) return false;
        return NormalizeForComparison(match.Groups["game"].Value) == NormalizeForComparison(gameFolder);
    }

    private static string NormalizeForComparison(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

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

            // No usable DB title (metadata missing or didn't decode) - falling
            // through to the raw filename stem would otherwise surface Medal's
            // own "MedalTV<Game><timestamp>" export name verbatim.
            if (title is null)
            {
                var fileStem = Path.GetFileNameWithoutExtension(videoPath);
                title = IsRawFilenameJustGameAndTimestamp(fileStem, gameFolder) ? gameFolder : TryParseTrimTitle(fileStem, gameFolder);
            }

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
