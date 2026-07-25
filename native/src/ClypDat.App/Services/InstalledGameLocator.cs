using System.Text.Json;
using Microsoft.Win32;

namespace ClypDat.App.Services;

// Finds the executable of an installed game without needing it to be running,
// so its real icon can be read straight off disk. Deliberately launcher-
// agnostic: the online lookup in GameIconService only knows Steam, which left
// Epic, Battle.net, EA, GOG and anything else with no artwork at all.
//
// Two sources, both of which every launcher ultimately feeds:
//  - Epic writes a JSON manifest per installed title, naming its executable.
//  - Everything else registers an uninstall entry whose DisplayIcon points at
//    the game's exe (that is the icon Windows itself shows for it).
public static class InstalledGameLocator
{
    private static Dictionary<string, string>? _index;

    // Built once per session - scanning manifests and three registry hives is
    // cheap but pointless to repeat, and installs rarely appear mid-session.
    public static IReadOnlyDictionary<string, string> Index => _index ??= BuildIndex();

    public static string? FindExecutable(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        return Index.TryGetValue(Normalize(displayName), out var path) ? path : null;
    }

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // A launcher/bootstrapper stub carries the LAUNCHER's icon, not the game's -
    // Epic's manifest for Fortnite points at FortniteBootstrapper.exe, which
    // yields the Epic Games logo. Where the named executable is one of those,
    // look for the real game binary beside it instead.
    private static readonly string[] StubExecutableMarkers =
    {
        "bootstrapper", "launcher", "unins", "setup", "helper", "crashreport",
        "epicgames", "eac", "easyanticheat", "battleye", "eos"
    };

    private static string PreferGameExecutable(string executablePath, string gameName)
    {
        try
        {
            var currentName = Normalize(Path.GetFileNameWithoutExtension(executablePath));
            if (!StubExecutableMarkers.Any(marker => currentName.Contains(marker, StringComparison.Ordinal))) return executablePath;

            var folder = Path.GetDirectoryName(executablePath);
            if (folder is null || !Directory.Exists(folder)) return executablePath;

            var normalizedGame = Normalize(gameName);
            var best = Directory.EnumerateFiles(folder, "*.exe")
                .Select(path => new { Path = path, Name = Normalize(Path.GetFileNameWithoutExtension(path)) })
                // Anti-cheat shims and the stub itself are never the game.
                .Where(candidate => !StubExecutableMarkers.Any(marker => candidate.Name.Contains(marker, StringComparison.Ordinal)))
                .OrderByDescending(candidate => candidate.Name.Contains(normalizedGame, StringComparison.Ordinal))
                .ThenByDescending(candidate => candidate.Name.Contains("shipping", StringComparison.Ordinal) || candidate.Name.Contains("client", StringComparison.Ordinal))
                // The real game binary is typically by far the largest.
                .ThenByDescending(candidate => new FileInfo(candidate.Path).Length)
                .FirstOrDefault();

            return best?.Path ?? executablePath;
        }
        catch
        {
            return executablePath;
        }
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            AddEpicGames(index);
        }
        catch (Exception error)
        {
            AppLog.Error("Epic manifest scan failed (non-fatal)", error);
        }

        try
        {
            AddUninstallEntries(index);
        }
        catch (Exception error)
        {
            AppLog.Error("Uninstall-registry scan failed (non-fatal)", error);
        }

        AppLog.Info($"Installed-game index built: {index.Count} entries.");
        return index;
    }

    private static void AddEpicGames(Dictionary<string, string> index)
    {
        var manifestFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestFolder)) return;

        foreach (var file in Directory.EnumerateFiles(manifestFolder, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (!root.TryGetProperty("DisplayName", out var nameElement)) continue;
                if (!root.TryGetProperty("InstallLocation", out var locationElement)) continue;
                if (!root.TryGetProperty("LaunchExecutable", out var executableElement)) continue;

                var name = nameElement.GetString();
                var location = locationElement.GetString();
                var executable = executableElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(executable)) continue;

                // Manifests use forward slashes inside LaunchExecutable.
                var full = Path.GetFullPath(Path.Combine(location, executable.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(full)) index.TryAdd(Normalize(name), PreferGameExecutable(full, name));
            }
            catch
            {
                // A single unreadable/partial manifest shouldn't stop the scan.
            }
        }
    }

    private static void AddUninstallEntries(Dictionary<string, string> index)
    {
        (RegistryKey Hive, string Path)[] roots =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, path) in roots)
        {
            using var uninstall = hive.OpenSubKey(path);
            if (uninstall is null) continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(subKeyName);
                    var name = entry?.GetValue("DisplayName") as string;
                    var icon = entry?.GetValue("DisplayIcon") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(icon)) continue;

                    // DisplayIcon is "path" or "path,index" and is sometimes quoted.
                    var iconPath = icon.Trim().Trim('"');
                    var comma = iconPath.LastIndexOf(',');
                    if (comma > 2) iconPath = iconPath[..comma];
                    iconPath = iconPath.Trim().Trim('"');

                    // .ico files aren't readable by the executable-icon path, and
                    // uninstaller stubs are the wrong icon for the game anyway.
                    if (!iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(iconPath).Contains("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(iconPath)) continue;

                    index.TryAdd(Normalize(name), PreferGameExecutable(iconPath, name));
                }
                catch
                {
                    // Skip entries we can't read rather than abandoning the hive.
                }
            }
        }
    }
}
