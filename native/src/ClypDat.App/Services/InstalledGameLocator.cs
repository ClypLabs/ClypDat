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
    //
    // Shared with SteamGameLibrary's executable-name index, which needs the
    // same "this binary is a shim, not the game" judgement when deciding which
    // names are safe to match a running process against.
    internal static readonly string[] StubExecutableMarkers =
    {
        "bootstrapper", "launcher", "unins", "setup", "helper", "crashreport",
        "epicgames", "eac", "easyanticheat", "battleye", "eos"
    };

    internal static bool LooksLikeStubExecutable(string executableName) =>
        StubExecutableMarkers.Any(marker => Normalize(executableName).Contains(marker, StringComparison.Ordinal));

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

        try
        {
            AddStartMenuShortcuts(index);
        }
        catch (Exception error)
        {
            AppLog.Error("Start-menu shortcut scan failed (non-fatal)", error);
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

    // Every launcher puts its games in the Start menu, whatever store they came
    // from - Riot, EA, Ubisoft, GOG, Battle.net and standalone installers alike
    // - so the shortcuts there cover the games the Epic manifests and the
    // uninstall registry between them still miss. The shortcut's own name is
    // the game's name as the user knows it, which is exactly the key icons are
    // looked up under.
    private static void AddStartMenuShortcuts(Dictionary<string, string> index)
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(shortcut);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var key = Normalize(name);
                    // Whatever the Epic manifests and the registry found is
                    // more precise than a shortcut, so they win.
                    if (key.Length == 0 || index.ContainsKey(key)) continue;

                    var target = ResolveShortcutTarget(shortcut);
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    // Windows' own accessories are not games and would happily
                    // claim names like "Paint" or "Notepad".
                    if (target.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(target).Contains("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(target)) continue;

                    index[key] = PreferGameExecutable(target, name);
                }
                catch
                {
                    // One unreadable shortcut shouldn't stop the sweep.
                }
            }
        }
    }

    // COM rather than parsing the .lnk binary: shortcut files carry their
    // target in several different structures depending on how they were
    // created, and the shell already knows how to read all of them.
    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        ShellLink? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new System.Text.StringBuilder(260);
            // SLGP_RAWPATH (4) - the path as stored, without the shell
            // resolving a moved target over the network, which can block.
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Only GetPath is declared because it is first in the vtable; the rest
        // of IShellLinkW is never called.
        void GetPath(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder file,
            int maxPath,
            IntPtr findData,
            uint flags);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("0000010b-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        // Declared in vtable order (IPersist::GetClassID first, then
        // IPersistFile's own) so Load lands on the right slot.
        void GetClassID(out Guid classId);
        [System.Runtime.InteropServices.PreserveSig] int IsDirty();
        void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string? fileName, bool remember);
        void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string? fileName);
        void GetCurFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string fileName);
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

                    // .ico is accepted alongside .exe - ExtractIconEx reads
                    // icon files too, and plenty of launchers (Steam writes
                    // one per game under steam\games) point DisplayIcon at an
                    // .ico rather than the binary. Skipping those was why an
                    // installed game could still end up with a letter badge.
                    // Uninstaller stubs stay out: wrong icon for the game.
                    var isIcon = iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
                    if (!isIcon && !iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(iconPath).Contains("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(iconPath)) continue;

                    index.TryAdd(Normalize(name), isIcon ? iconPath : PreferGameExecutable(iconPath, name));
                }
                catch
                {
                    // Skip entries we can't read rather than abandoning the hive.
                }
            }
        }
    }
}
