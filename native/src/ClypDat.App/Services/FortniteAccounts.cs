using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

// Who "you" are in a Fortnite replay. The replay's own owner record lives in
// a PlayerState actor ClypDat does not decode (see FortniteReplayParser), so
// the player is matched by Epic account id instead: Fortnite keeps a cloud
// settings folder per account that has played on this PC, and its log names
// the signed-in account. All local files, read-only.
internal static partial class FortniteAccounts
{
    private const long MaximumLogBytes = 64L * 1024 * 1024;
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _names;
    private static string? _namesFolder;
    private static DateTime _namesReadUtc;

    public static string DefaultSavedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved");

    /// <summary>Upper-case 32-hex Epic account ids that have played Fortnite on this PC.</summary>
    public static IReadOnlyList<string> LocalAccountIds(string savedFolder)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cloud = Path.Combine(savedFolder, "Cloud");
            if (Directory.Exists(cloud))
                foreach (var folder in Directory.EnumerateDirectories(cloud))
                {
                    var name = Path.GetFileName(folder);
                    if (IsAccountId(name)) ids.Add(name.ToUpperInvariant());
                }
            var settings = Path.Combine(savedFolder, "Config", "WindowsClient", "GameUserSettings.ini");
            if (File.Exists(settings) && new FileInfo(settings).Length < 4 * 1024 * 1024)
                foreach (var line in ReadLinesShared(settings))
                    if (line.StartsWith("EulaAcceptedUserId=", StringComparison.OrdinalIgnoreCase) && line[19..].Trim() is var id && IsAccountId(id))
                        ids.Add(id.ToUpperInvariant());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"Fortnite accounts: not readable ({error.GetType().Name}).");
        }
        return ids.ToArray();
    }

    /// <summary>The display name Fortnite last logged for an account, or null.</summary>
    public static string? DisplayName(string savedFolder, string accountId)
    {
        lock (Gate)
        {
            // First use, or a name logged since: read the logs again, but not
            // on every replay of a backfill.
            if (_names is null || !string.Equals(_namesFolder, savedFolder, StringComparison.OrdinalIgnoreCase) ||
                DateTime.UtcNow - _namesReadUtc > TimeSpan.FromMinutes(5))
            {
                _names = ReadDisplayNames(Path.Combine(savedFolder, "Logs"));
                _namesReadUtc = DateTime.UtcNow;
                _namesFolder = savedFolder;
            }
            return _names.GetValueOrDefault(accountId);
        }
    }

    internal static Dictionary<string, string> ReadDisplayNames(string logsFolder)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(logsFolder)) return names;
            // Oldest first, so the newest log's name wins.
            foreach (var log in Directory.EnumerateFiles(logsFolder, "FortniteGame*.log").OrderBy(File.GetLastWriteTimeUtc))
            {
                if (new FileInfo(log).Length > MaximumLogBytes) continue;
                foreach (var line in ReadLinesShared(log))
                {
                    if (!line.Contains("EpicAccountId=[MCP:", StringComparison.Ordinal)) continue;
                    foreach (Match match in DisplayNamePattern().Matches(line))
                        names[match.Groups["id"].Value.ToUpperInvariant()] = match.Groups["name"].Value.Trim();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"Fortnite accounts: logs not readable ({error.GetType().Name}).");
        }
        return names;
    }

    internal static bool IsAccountId(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    [GeneratedRegex(@"DisplayName=\[(?<name>[^\]\r\n]{1,64})\] EpicAccountId=\[MCP:(?<id>[0-9a-fA-F]{32})\]")]
    private static partial Regex DisplayNamePattern();
}
