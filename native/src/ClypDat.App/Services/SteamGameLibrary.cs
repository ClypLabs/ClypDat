using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record SteamGameInstall(int AppId, string DisplayName, string InstallPath);

public sealed class SteamGameLibrary
{
    private static readonly Regex VdfPath = new("\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestValue = new("\\\"(?<key>appid|name|installdir)\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly object _sync = new();
    private IReadOnlyList<SteamGameInstall> _installs = Array.Empty<SteamGameInstall>();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public SteamGameInstall? FindByExecutablePath(string? executablePath)
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
            catch (Exception error) { AppLog.Error("Steam game library scan failed", error); }
            finally { _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5); }
        }
    }

    private static IReadOnlyList<SteamGameInstall> LoadInstalls()
    {
        var steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath)) return Array.Empty<SteamGameInstall>();

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            foreach (Match match in VdfPath.Matches(File.ReadAllText(libraryFile)))
            {
                var path = match.Groups["path"].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) libraries.Add(path);
            }
        }

        var games = new List<SteamGameInstall>();
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var values = ManifestValue.Matches(File.ReadAllText(manifestPath))
                    .Cast<Match>()
                    .GroupBy(match => match.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
                if (!values.TryGetValue("appid", out var idText) || !int.TryParse(idText, out var appId) ||
                    !values.TryGetValue("name", out var name) || !values.TryGetValue("installdir", out var installDir) ||
                    string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                var installPath = Path.Combine(steamApps, "common", installDir);
                if (Directory.Exists(installPath)) games.Add(new SteamGameInstall(appId, name, installPath));
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
