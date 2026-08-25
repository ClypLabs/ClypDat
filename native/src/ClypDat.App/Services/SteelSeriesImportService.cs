using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ClypDat.App.Services;

public sealed record SteelSeriesClipRecord(
    string VideoPath,
    string? ThumbnailPath,
    string GameName,
    DateTimeOffset CapturedAt,
    string Title,
    string? CatalogId = null,
    bool IsLegacyFallback = false,
    bool IsTrimmed = false,
    string? AutoClipEventType = null,
    bool HasMeaningfulTitle = true);

public sealed record SteelSeriesScanProgress(double Percent, string Status);

public static class SteelSeriesImportService
{
    private static readonly Regex TimestampedNamePattern = new(
        @"^(?<game>.+)__(?<date>20\d{2}-\d{2}-\d{2})__(?<time>\d{2}-\d{2}-\d{2})(?:_trim)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GenericTitlePattern = new(
        @"^(?:Moments (?:Desktop )?clip|Trimmed clip) from .+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string GetImportKey(SteelSeriesClipRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CatalogId)) return $"id|{record.CatalogId}";
        long length;
        try { length = new FileInfo(record.VideoPath).Length; }
        catch { length = -1; }
        var pathPart = record.IsLegacyFallback
            ? Path.GetFullPath(record.VideoPath).Replace('\\', '/').ToLowerInvariant()
            : string.Empty;
        return $"v1|{record.CapturedAt.UtcTicks}|{length}|{pathPart}";
    }

    public static string? GetCaptureRootFromDatabase()
    {
        var db = DatabasePath;
        if (!File.Exists(db)) return null;
        // C:\ProgramData lets any user create a missing directory chain and own what
        // they create, so a machine with no SteelSeries GG install is one where this
        // database can be planted. Only trust one an installer actually created.
        if (!ImportSourceGuard.IsTrustedMachineWideDatabase(db)) return null;
        try
        {
            using var connection = OpenReadOnly(db);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM moments_settings WHERE key = 'captureDir' LIMIT 1";
            return command.ExecuteScalar()?.ToString();
        }
        catch (Exception error)
        {
            AppLog.Error($"SteelSeries import: failed reading capture folder from {db}", error);
            return null;
        }
    }

    public static IReadOnlyList<SteelSeriesClipRecord> ScanForClips(IProgress<SteelSeriesScanProgress>? progress = null)
    {
        var results = new List<SteelSeriesClipRecord>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = GetCaptureRootFromDatabase();
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SteelSeries Moments");
        progress?.Report(new SteelSeriesScanProgress(0, "Finding SteelSeries Moments clips..."));

        var catalogRead = TryReadCatalog(results, seenPaths, progress);
        progress?.Report(new SteelSeriesScanProgress(catalogRead ? 40 : 10, catalogRead ? "Reading SteelSeries catalog complete." : "SteelSeries catalog unavailable; scanning files..."));
        ScanFiles(root, results, seenPaths, progress);
        progress?.Report(new SteelSeriesScanProgress(100, "SteelSeries Moments scan complete."));
        return results.GroupBy(GetImportKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    private static bool TryReadCatalog(List<SteelSeriesClipRecord> results, HashSet<string> seenPaths, IProgress<SteelSeriesScanProgress>? progress)
    {
        if (!File.Exists(DatabasePath)) return false;
        if (!ImportSourceGuard.IsTrustedMachineWideDatabase(DatabasePath)) return false;
        try
        {
            using var connection = OpenReadOnly(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,name,path,recording_timestamp,thumbnail_path,last_game_name,is_deleted,is_manually_trimmed,trigger_type,trigger_name FROM moments_clips WHERE path IS NOT NULL";
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                count++;
                if (reader.GetInt64(6) != 0) continue;
                var path = reader.GetString(2);
                if (IsSavedClipsPath(path)) continue;
                // The path is whatever the database says. Importing copies it into the
                // library - or MOVES it when the copy toggle is off - so a UNC path, a
                // removable drive, or a symlink is not something to act on.
                if (!ImportSourceGuard.IsAllowedSourcePath(path)) continue;
                if (!File.Exists(path) || !seenPaths.Add(path)) continue;
                var fileStem = Path.GetFileNameWithoutExtension(path);
                var hasFilenameTimestamp = TryParseTimestampedName(fileStem, out _, out var filenameCapturedAt);
                if (!TryParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3), out var metadataCapturedAt) && !hasFilenameTimestamp) continue;
                var capturedAt = hasFilenameTimestamp ? filenameCapturedAt : metadataCapturedAt;
                var game = NormalizeGame(reader.IsDBNull(5) ? null : reader.GetString(5));
                if (string.IsNullOrWhiteSpace(game)) TryParseTimestampedName(Path.GetFileNameWithoutExtension(path), out game, out _);
                game = string.IsNullOrWhiteSpace(game) ? "Unknown Game" : game;
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var isTrimmed = !reader.IsDBNull(7) && reader.GetInt64(7) != 0;
                var title = NormalizeTitle(name, game, fileStem, isTrimmed);
                var thumbnail = reader.IsDBNull(4) ? null : reader.GetString(4);
                var triggerType = reader.IsDBNull(8) ? null : reader.GetString(8);
                var triggerName = reader.IsDBNull(9) ? null : reader.GetString(9);
                results.Add(new SteelSeriesClipRecord(path, thumbnail, game, capturedAt, title, reader.GetString(0), IsTrimmed: isTrimmed,
                    AutoClipEventType: GetAutoClipEventType(title, triggerType, triggerName),
                    HasMeaningfulTitle: HasMeaningfulTitle(name, game, fileStem)));
                progress?.Report(new SteelSeriesScanProgress(Math.Min(40, count / 307d * 40), $"Reading SteelSeries catalog clip {count}..."));
            }
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"SteelSeries import: failed reading {DatabasePath}", error);
            return false;
        }
    }

    private static void ScanFiles(string root, List<SteelSeriesClipRecord> results, HashSet<string> seenPaths, IProgress<SteelSeriesScanProgress>? progress)
    {
        if (!Directory.Exists(root)) return;
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => !IsSavedClipsPath(path) && MediaProbeService.IsVideoFile(path)).ToArray();
        }
        catch (Exception error)
        {
            AppLog.Error($"SteelSeries import: failed scanning {root}", error);
            return;
        }

        for (var i = 0; i < files.Length; i++)
        {
            var path = files[i];
            if (!seenPaths.Add(path)) continue;
            SteelSeriesClipRecord record;
            if (TryReadEmbeddedMetadata(path, out var embedded))
            {
                record = embedded;
            }
            else if (TryParseTimestampedName(Path.GetFileNameWithoutExtension(path), out var game, out var capturedAt))
            {
                record = new SteelSeriesClipRecord(path, null, NormalizeGame(game) ?? "Unknown Game", capturedAt,
                    NormalizeTitle(Path.GetFileNameWithoutExtension(path), NormalizeGame(game) ?? "Unknown Game", Path.GetFileNameWithoutExtension(path)),
                    HasMeaningfulTitle: false);
            }
            else
            {
                DateTimeOffset fallback;
                try { fallback = new DateTimeOffset(File.GetLastWriteTime(path)); }
                catch { fallback = DateTimeOffset.Now; }
                record = new SteelSeriesClipRecord(path, null, "Unknown Game", fallback,
                    Path.GetFileNameWithoutExtension(path), IsLegacyFallback: true,
                    HasMeaningfulTitle: HasMeaningfulTitle(Path.GetFileNameWithoutExtension(path), "Unknown Game", Path.GetFileNameWithoutExtension(path)));
            }
            results.Add(record);
            progress?.Report(new SteelSeriesScanProgress(40 + 60d * (i + 1) / Math.Max(1, files.Length), $"Scanning SteelSeries file {i + 1} of {files.Length}..."));
        }
    }

    internal static string NormalizeTitle(string? title, string game, string fileStem, bool isTrimmed = false)
    {
        title = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title)) return fileStem;
        if (TryParseTimestampedName(title, out _, out _)) return title.EndsWith("_trim", StringComparison.OrdinalIgnoreCase) ? $"{game} (Trimmed)" : game;
        if (title.StartsWith("Trimmed clip from ", StringComparison.OrdinalIgnoreCase)) return $"{game} (Trimmed)";
        if (GenericTitlePattern.IsMatch(title)) return isTrimmed ? $"{game} (Trimmed)" : game;
        return title;
    }

    internal static string? NormalizeGame(string? game)
    {
        if (string.IsNullOrWhiteSpace(game) || game.Equals("moments.desktopModeGameName", StringComparison.OrdinalIgnoreCase)) return null;
        if (game.Equals("DESKTOPCAPTURE", StringComparison.OrdinalIgnoreCase)) return "Desktop Capture";
        return game.Trim();
    }

    internal static bool TryParseTimestampedName(string stem, out string game, out DateTimeOffset capturedAt)
    {
        var match = TimestampedNamePattern.Match(stem);
        if (match.Success && DateTime.TryParseExact($"{match.Groups["date"].Value} {match.Groups["time"].Value}", "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            game = match.Groups["game"].Value.Replace('-', ' ').Trim();
            capturedAt = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
            return true;
        }
        game = string.Empty;
        capturedAt = default;
        return false;
    }

    private static bool TryParseTimestamp(string? text, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp);

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static string DatabasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SteelSeries", "GG", "apps", "moments", "db", "database.db");
    internal static bool IsSavedClipsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "Saved Clips", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadEmbeddedMetadata(string path, out SteelSeriesClipRecord record)
    {
        record = null!;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegPathResolver.FfprobePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
                }
            };
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("quiet");
            process.StartInfo.ArgumentList.Add("-print_format");
            process.StartInfo.ArgumentList.Add("json");
            process.StartInfo.ArgumentList.Add("-show_entries");
            process.StartInfo.ArgumentList.Add("format_tags");
            process.StartInfo.ArgumentList.Add(path);
            if (!process.Start()) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return false;
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("format", out var format) ||
                !format.TryGetProperty("tags", out var tags)) return false;
            var metadataJson = string.Concat(tags.EnumerateObject()
                .Where(property => property.Name.StartsWith("STEELSERIES_META", StringComparison.OrdinalIgnoreCase))
                .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(property => property.Value.ToString()));
            if (string.IsNullOrWhiteSpace(metadataJson)) return false;
            using var metadata = JsonDocument.Parse(metadataJson);
            var root = metadata.RootElement;
            if (!TryParseTimestamp(root.TryGetProperty("recording_timestamp", out var timestamp) ? timestamp.GetString() : null, out var metadataCapturedAt)) return false;
            var fileStem = Path.GetFileNameWithoutExtension(path);
            var capturedAt = TryParseTimestampedName(fileStem, out _, out var filenameCapturedAt) ? filenameCapturedAt : metadataCapturedAt;
            var game = NormalizeGame(root.TryGetProperty("last_game_name", out var gameValue) ? gameValue.GetString() : null)
                ?? (TryParseTimestampedName(fileStem, out var inferredGame, out _) ? NormalizeGame(inferredGame) : null)
                ?? "Unknown Game";
            var isTrimmed = root.TryGetProperty("is_manually_trimmed", out var trimmedValue) && trimmedValue.ValueKind == JsonValueKind.True;
            var nameText = root.TryGetProperty("name", out var name) ? name.GetString() : null;
            var title = NormalizeTitle(nameText, game, fileStem, isTrimmed);
            var thumbnail = root.TryGetProperty("thumbnail_path", out var thumbnailValue) ? thumbnailValue.GetString() : null;
            var triggerType = root.TryGetProperty("trigger_type", out var triggerTypeValue) ? triggerTypeValue.GetString() : null;
            var triggerName = root.TryGetProperty("trigger_name", out var triggerNameValue) ? triggerNameValue.GetString() : null;
            var autoclipTrigger = root.TryGetProperty("autoclip_trigger", out var autoclipTriggerValue) ? autoclipTriggerValue.GetString() : null;
            record = new SteelSeriesClipRecord(path, thumbnail, game, capturedAt, title, IsTrimmed: isTrimmed,
                AutoClipEventType: GetAutoClipEventType(title, triggerType, triggerName ?? autoclipTrigger),
                HasMeaningfulTitle: HasMeaningfulTitle(nameText, game, fileStem));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? GetAutoClipEventType(string? title, string? triggerType, string? triggerName)
    {
        var isAutoClip = string.Equals(triggerType, "auto", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(triggerName) && !string.Equals(triggerName, "shortcut", StringComparison.OrdinalIgnoreCase))
            || title?.StartsWith("Auto-clip:", StringComparison.OrdinalIgnoreCase) == true;
        if (!isAutoClip) return null;

        if (!string.IsNullOrWhiteSpace(title) && title.StartsWith("Auto-clip:", StringComparison.OrdinalIgnoreCase))
        {
            var reason = title["Auto-clip:".Length..].Trim();
            if (reason.Length > 0) return reason;
        }

        if (string.IsNullOrWhiteSpace(triggerName)) return "Auto-clip";
        return string.Join(' ', triggerName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    internal static bool HasMeaningfulTitle(string? title, string game, string fileStem)
    {
        title = title?.Trim() ?? string.Empty;
        if (title.Length == 0 || GenericTitlePattern.IsMatch(title) || TryParseTimestampedName(title, out _, out _)) return false;
        return !title.Equals(game, StringComparison.OrdinalIgnoreCase);
    }
}
