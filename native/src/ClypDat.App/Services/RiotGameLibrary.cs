using System.Text.Json;

namespace ClypDat.App.Services;

public sealed record RiotGameInstall(string DisplayName, string InstallPath);

// Riot has no per-game uninstall entry or Epic-style manifest either. The
// Riot Client tracks every product it manages in one shared file whose
// top-level keys are each product's own install directory, so the same
// trick as Battle.net applies: the folder name IS the game's name (e.g.
// "C:\Riot Games\VALORANT"). That file's schema isn't officially published,
// so this also falls back to scanning the default Riot Games root directly
// in case the JSON is missing, stale, or its shape has moved on.
public sealed class RiotGameLibrary
{
    private readonly object _sync = new();
    private IReadOnlyList<RiotGameInstall> _installs = Array.Empty<RiotGameInstall>();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public RiotGameInstall? FindByExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        EnsureLoaded();
        return _installs.FirstOrDefault(game => IsUnderPath(executablePath, game.InstallPath));
    }

    private void EnsureLoaded()
    {
        if (DateTime.UtcNow < _nextRefreshUtc) return;
        lock (_sync)
        {
            if (DateTime.UtcNow < _nextRefreshUtc) return;
            try { _installs = LoadInstalls(); }
            catch (Exception error) { AppLog.Error("Riot game library scan failed", error); }
            finally { _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5); }
        }
    }

    private static IReadOnlyList<RiotGameInstall> LoadInstalls()
    {
        var games = new Dictionary<string, RiotGameInstall>(StringComparer.OrdinalIgnoreCase);

        var installsJson = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games", "RiotClientInstalls.json");
        if (File.Exists(installsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(installsJson));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    AddIfRiotGamePath(games, property.Name);
                    AddManifestPaths(games, property.Value, property.Name);
                }
            }
            catch
            {
                // Malformed/locked file - the directory scan below still covers it.
            }
        }

        var defaultRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games");
        if (Directory.Exists(defaultRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(defaultRoot))
            {
                AddIfRiotGamePath(games, folder);
            }
        }

        return games.Values.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    private static void AddManifestPaths(Dictionary<string, RiotGameInstall> games, JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            if (propertyName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                propertyName.Contains("dir", StringComparison.OrdinalIgnoreCase) ||
                propertyName.Contains("install", StringComparison.OrdinalIgnoreCase))
            {
                AddIfRiotGamePath(games, element.GetString());
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) AddManifestPaths(games, property.Value, property.Name);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AddManifestPaths(games, item, propertyName);
        }
    }

    private static void AddIfRiotGamePath(Dictionary<string, RiotGameInstall> games, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var marker = Path.DirectorySeparatorChar + "Riot Games" + Path.DirectorySeparatorChar;
            var markerIndex = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var afterRoot = full[(markerIndex + marker.Length)..];
                var gameName = afterRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    var riotRoot = full[..(markerIndex + marker.Length - 1)];
                    AddIfGameFolder(games, Path.Combine(riotRoot, gameName));
                    return;
                }
            }
        }
        catch { }
        AddIfGameFolder(games, path);
    }

    private static void AddIfGameFolder(Dictionary<string, RiotGameInstall> games, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        // The client's own install folder, not a game.
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase)) return;
        var displayName = name.ToUpperInvariant() switch
        {
            "VALORANT" => "Valorant",
            "LEAGUE OF LEGENDS" => "League of Legends",
            _ => name
        };
        games.TryAdd(full, new RiotGameInstall(displayName, full));
    }

    private static bool IsUnderPath(string candidate, string root)
    {
        try
        {
            var normalizedCandidate = Path.GetFullPath(candidate);
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
