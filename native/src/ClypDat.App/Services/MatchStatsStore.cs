using System.Collections.Concurrent;
using System.Text.Json;

namespace ClypDat.App.Services;

// Match summaries live with the library's other metadata, in
// .clipinfo/matches, one JSON file per match. They are not tied to any one
// clip: a match covers many clips and recordings, which find their match by
// wall-clock overlap when opened (see MatchClipMapping).
internal sealed class MatchStatsStore(string libraryRoot)
{
    private const long MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, MatchStats Match)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public string LibraryRoot { get; } = libraryRoot;
    public string Folder => Path.Combine(LibraryLayout.ClipInfoRoot(LibraryRoot), "matches");

    public void Save(MatchStats match)
    {
        var path = PathFor(match.Id);
        if (!LibraryPathGuard.IsWithin(LibraryRoot, path)) throw new InvalidDataException("Match metadata crosses a filesystem link.");
        LibraryLayout.EnsureClipInfoRoot(LibraryRoot);
        Directory.CreateDirectory(Folder);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(match, Options));
        File.Move(temporary, path, overwrite: true);
        Cache[path] = (File.GetLastWriteTimeUtc(path), match);
    }

    public IReadOnlyList<MatchStats> LoadAll()
    {
        var folder = Folder;
        if (!Directory.Exists(folder) || !LibraryPathGuard.IsWithin(LibraryRoot, folder)) return [];
        var matches = new List<MatchStats>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaximumFileBytes) continue;
                if (Cache.TryGetValue(path, out var cached) && cached.WriteUtc == info.LastWriteTimeUtc) { matches.Add(cached.Match); continue; }
                var match = JsonSerializer.Deserialize<MatchStats>(File.ReadAllText(path), Options);
                if (match is null || match.Version > MatchStats.CurrentVersion) continue;
                Cache[path] = (info.LastWriteTimeUtc, match);
                matches.Add(match);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Info($"Match stats: skipped an unreadable match file ({error.GetType().Name}).");
            }
        }
        return matches;
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        Cache.TryRemove(path, out _);
        if (LibraryPathGuard.IsWithin(LibraryRoot, path) && File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string id)
    {
        var safe = new string(id.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(96).ToArray());
        if (safe.Length == 0) throw new ArgumentException("Match id is empty.", nameof(id));
        return Path.Combine(Folder, safe + ".json");
    }
}
