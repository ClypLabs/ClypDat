using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record SteamGameInstall(int AppId, string DisplayName, string InstallPath);

public sealed class SteamGameLibrary
{
    private static readonly Regex VdfPath = new("\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestValue = new("\\\"(?<key>appid|name|installdir)\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly object _sync = new();
    private IReadOnlyList<SteamGameInstall> _installs = Array.Empty<SteamGameInstall>();
    private volatile Dictionary<string, SteamGameInstall>? _executableIndex;
    private bool _executableIndexBuilding;
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public SteamGameInstall? FindByExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        EnsureLoaded();
        return _installs.FirstOrDefault(game => IsUnderPath(executablePath, game.InstallPath));
    }

    // Fallback for a game whose running executable is NOT under its own
    // library folder, which the path test above can never match. Launchers
    // relocate binaries routinely - Ubisoft Connect runs Rainbow Six Siege out
    // of %LOCALAPPDATA%\Ubisoft\r6s - and with the remote catalog carrying
    // effectively nothing, a hand-written entry per affected title does not
    // scale. The same executable name almost always still exists inside the
    // install folder, so matching on that recovers the game without needing to
    // know anything about the launcher.
    public SteamGameInstall? FindByExecutableName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return null;
        EnsureLoaded();
        var index = _executableIndex;
        if (index is null)
        {
            StartExecutableIndexBuild();
            return null;
        }

        return index.TryGetValue(executableName, out var game) ? game : null;
    }

    // Built off-thread, and callers get null until it is ready. This is
    // deliberate: detection runs on a DispatcherTimer, i.e. the UI thread, and
    // walking every install folder takes long enough (hundreds of ms, seconds
    // on a cold cache with 40+ games) that doing it inline would freeze the
    // app - the same failure as the process-table walk that once locked up the
    // machine at logon. A game therefore resolves a tick or two after launch
    // rather than instantly, which nothing here is sensitive to.
    private void StartExecutableIndexBuild()
    {
        lock (_sync)
        {
            if (_executableIndexBuilding || _executableIndex is not null) return;
            _executableIndexBuilding = true;
            var installs = _installs;
            _ = Task.Run(() =>
            {
                try
                {
                    var index = BuildExecutableIndex(installs);
                    lock (_sync)
                    {
                        _executableIndex = index;
                        _executableIndexBuilding = false;
                    }
                    AppLog.Debug($"Steam executable index built: {index.Count} unique executables across {installs.Count} installed games.");
                }
                catch (Exception error)
                {
                    lock (_sync) _executableIndexBuilding = false;
                    AppLog.Error("Steam executable index build failed", error);
                }
            });
        }
    }

    // Depth and file caps are the point of this method, not incidental. A full
    // recursive walk here would be a walk of every installed game's entire
    // directory tree (hundreds of GB) on the detection path - the same shape
    // of mistake as the process-table snapshot that used to sit in the
    // detection loop and locked up the machine at logon. Game binaries live at
    // the top of an install or one or two folders down (Win64/Shipping,
    // Binaries/Win64), so a shallow scan finds them without the risk.
    private static Dictionary<string, SteamGameInstall> BuildExecutableIndex(IReadOnlyList<SteamGameInstall> installs)
    {
        const int MaxDepth = 3;
        const int MaxFilesPerGame = 400;
        // Names seen in more than one game are ambiguous, so they are dropped
        // rather than guessed at - plenty of games ship an identically named
        // helper, and attributing a clip to the wrong game is worse than not
        // detecting it.
        var claimed = new Dictionary<string, SteamGameInstall?>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in installs)
        {
            foreach (var executable in EnumerateExecutables(game.InstallPath, MaxDepth, MaxFilesPerGame))
            {
                var name = Path.GetFileName(executable);
                // Shims and stubs are not the game, and are exactly the names
                // most likely to collide across installs.
                if (InstalledGameLocator.LooksLikeStubExecutable(name)) continue;
                if (claimed.TryGetValue(name, out var owner))
                {
                    if (owner is not null && owner.AppId != game.AppId) claimed[name] = null;
                    continue;
                }
                claimed[name] = game;
            }
        }

        return claimed
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateExecutables(string root, int maxDepth, int maxFiles)
    {
        var found = 0;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && found < maxFiles)
        {
            var (folder, depth) = queue.Dequeue();
            string[] files;
            try { files = Directory.GetFiles(folder, "*.exe"); }
            catch { continue; }
            foreach (var file in files)
            {
                if (found++ >= maxFiles) yield break;
                yield return file;
            }

            if (depth >= maxDepth) continue;
            string[] folders;
            try { folders = Directory.GetDirectories(folder); }
            catch { continue; }
            foreach (var child in folders) queue.Enqueue((child, depth + 1));
        }
    }

    private void EnsureLoaded()
    {
        if (DateTime.UtcNow < _nextRefreshUtc) return;
        lock (_sync)
        {
            if (DateTime.UtcNow < _nextRefreshUtc) return;
            try
            {
                _installs = LoadInstalls();
                // Dropped, not rebuilt - the next failed path match kicks off a
                // fresh build in the background, so a refresh that nobody
                // follows up on costs nothing. Not cleared mid-build, which
                // would let two builds race.
                if (!_executableIndexBuilding) _executableIndex = null;
            }
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
                var values = ManifestValue.Matches(ReadManifestText(manifestPath))
                    .Cast<Match>()
                    .GroupBy(match => match.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
                if (!values.TryGetValue("appid", out var idText) || !int.TryParse(idText, out var appId) ||
                    !values.TryGetValue("name", out var name) || !values.TryGetValue("installdir", out var installDir) ||
                    string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                // installdir comes straight out of a third-party appmanifest_*.acf, and
                // Path.Combine happily accepts both "..\..\" and a rooted path (which
                // discards the earlier segments entirely). Mis-attributing a process to a
                // game only changes a clip's folder name, but the containment check
                // already exists a few lines down - there is no reason not to use it.
                var installPath = Path.Combine(steamApps, "common", installDir);
                if (!IsUnderPath(installPath, Path.Combine(steamApps, "common"))) continue;
                if (Directory.Exists(installPath)) games.Add(new SteamGameInstall(appId, name, installPath));
            }
        }

        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    // File.ReadAllText(path) defaults to strict-ish UTF-8, which silently
    // swaps any invalid byte for U+FFFD instead of throwing - most
    // appmanifest_*.acf files are genuinely UTF-8, but some game names
    // (trademark/registered symbols especially) come through from Steam's own
    // metadata as a single-byte codepage, so a name like "Overwatch (R)"
    // decodes as "Overwatch<FFFD>" instead of "Overwatch(R)". Decoding
    // strictly first and only falling back on a real failure keeps the common
    // UTF-8 case untouched while fixing the mis-encoded one. Latin1 (not a
    // registered Windows-1252 provider, which .NET Core doesn't carry by
    // default) - same byte range as the printable symbols actually seen here.
    private static string ReadManifestText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
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
