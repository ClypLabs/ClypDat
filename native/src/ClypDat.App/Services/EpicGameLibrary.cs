using System.Text.Json;

namespace ClypDat.App.Services;

public sealed record EpicGameInstall(string DisplayName, string InstallPath);

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

                games.Add(new EpicGameInstall(name, location));
            }
            catch
            {
                // A single unreadable/partial manifest shouldn't stop the scan.
            }
        }

        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
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
