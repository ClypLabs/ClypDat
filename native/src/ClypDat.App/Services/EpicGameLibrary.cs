using System.Text.Json;

namespace ClypDat.App.Services;

public sealed record EpicGameInstall(string DisplayName, string InstallPath, bool IsPlayable = true);

// Same idea as SteamGameLibrary, for Epic: its .item manifests already name
// both the display name and the install folder, so a running exe just needs
// to be found under that folder.
public sealed class EpicGameLibrary
{
    private readonly object _sync = new();
    private IReadOnlyList<EpicGameInstall> _installs = Array.Empty<EpicGameInstall>();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public EpicGameInstall? FindByExecutablePath(string? executablePath)
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
            catch (Exception error) { AppLog.Error("Epic game library scan failed", error); }
            finally { _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5); }
        }
    }

    private static IReadOnlyList<EpicGameInstall> LoadInstalls()
    {
        var manifestFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestFolder)) return Array.Empty<EpicGameInstall>();

        var games = new List<EpicGameInstall>();
        foreach (var file in Directory.EnumerateFiles(manifestFolder, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (!root.TryGetProperty("DisplayName", out var nameElement)) continue;
                if (!root.TryGetProperty("InstallLocation", out var locationElement)) continue;

                var name = nameElement.GetString();
                var location = locationElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location)) continue;
                if (!Directory.Exists(location)) continue;

                // Fortnite ships alongside "LEGO Fortnite Content" and
                // "Fortnite Save the World Content", all three pointing at the
                // SAME install folder. Only the real game names an executable,
                // so that is what separates it from its content packs.
                var launchExecutable = root.TryGetProperty("LaunchExecutable", out var launchElement)
                    ? launchElement.GetString() ?? string.Empty
                    : string.Empty;
                games.Add(new EpicGameInstall(name, location, launchExecutable.Length > 0));
            }
            catch
            {
                // A single unreadable/partial manifest shouldn't stop the scan.
            }
        }

        // Longest path first so a game installed inside another game's folder
        // still wins, then playable entries ahead of content-only ones so a
        // path shared by both resolves to the game rather than to a DLC entry.
        return games
            .OrderByDescending(game => game.InstallPath.Length)
            .ThenByDescending(game => game.IsPlayable)
            .ToArray();
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
