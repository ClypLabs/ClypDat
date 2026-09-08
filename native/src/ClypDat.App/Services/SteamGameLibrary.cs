using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record SteamGameInstall(int AppId, string DisplayName, string InstallPath);

public sealed class SteamGameLibrary
{
    private static readonly Regex VdfPath = new("\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestValue = new("\\\"(?<key>appid|name|installdir)\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static SteamGameLibrary Shared { get; } = new();
    private readonly object _sync = new();
    private readonly Func<string?> _steamPath;
    private readonly Action? _beforeIndexBuild;
    private volatile SteamClassificationSnapshot _snapshot = SteamClassificationSnapshot.Empty;
    private Task? _refreshTask;
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public SteamGameLibrary() : this(() => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string) { }
    internal SteamGameLibrary(Func<string?> steamPath, Action? beforeIndexBuild = null)
    {
        _steamPath = steamPath;
        _beforeIndexBuild = beforeIndexBuild;
    }

    public event Action? Changed;
    public SteamClassificationSnapshot Snapshot { get { _ = RefreshAsync(); return _snapshot; } }

    public SteamGameInstall? FindByExecutablePath(string? path) => Snapshot.FindGameByPath(path);
    public SteamGameInstall? FindByExecutableName(string? name) => Snapshot.FindGameByName(name);
    public bool IsSoftware(string? path = null, string? executableName = null, string? detectionKey = null) =>
        Snapshot.IsSoftware(path, executableName, detectionKey);

    internal Task RefreshAsync(bool force = false)
    {
        lock (_sync)
        {
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            if (!force && DateTime.UtcNow < _nextRefreshUtc) return Task.CompletedTask;
            _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5);
            return _refreshTask = Task.Run(() =>
            {
                try
                {
                    var root = _steamPath();
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
                    var previous = _snapshot;
                    var metadataPath = Path.Combine(root, "appcache", "appinfo.vdf");
                    var metadataStamp = File.GetLastWriteTimeUtc(metadataPath);
                    System.Collections.Frozen.FrozenDictionary<int, SteamAppKind> kinds;
                    var hasMetadata = false;
                    try { kinds = SteamAppInfoReader.Read(metadataPath); hasMetadata = true; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        AppLog.Debug($"Steam classification read failed: {error.Message}");
                        if (previous.HasMetadata) return; // retain the entire last valid snapshot
                        kinds = previous.Kinds;
                    }
                    var installs = LoadInstalls(root);
                    // One serialized worker builds and publishes all indexes together. No
                    // index from an older classification can overwrite newer metadata.
                    _beforeIndexBuild?.Invoke();
                    var names = BuildExecutableIndex(installs);
                    if (File.GetLastWriteTimeUtc(metadataPath) != metadataStamp)
                    {
                        lock (_sync) _nextRefreshUtc = DateTime.MinValue;
                        return; // metadata changed during the directory walk; retry next tick
                    }
                    var next = new SteamClassificationSnapshot(installs, kinds, names, hasMetadata);
                    if (previous.EquivalentTo(next)) return;
                    _snapshot = next;
                    Changed?.Invoke();
                }
                catch (Exception error) { AppLog.Error("Steam game library scan failed", error); }
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
    private static Dictionary<string, SteamGameInstall?> BuildExecutableIndex(IReadOnlyList<SteamGameInstall> installs)
    {
        const int MaxDepth = 3;
        const int MaxFilesPerGame = 400;
        // Keep ambiguous names as null, including collisions with software and
        // unknown installs. Neither game matching nor software exclusion may
        // guess an owner from a shared filename.
        var claimed = new Dictionary<string, SteamGameInstall?>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in installs)
        {
            foreach (var executable in EnumerateExecutables(game.InstallPath, MaxDepth, MaxFilesPerGame))
            {
                // A nested install owns its files, not the enclosing install.
                if (installs.Any(other => other.InstallPath.Length > game.InstallPath.Length && IsUnderPath(executable, other.InstallPath))) continue;
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

        return claimed;
    }

    private static IEnumerable<string> EnumerateExecutables(string root, int maxDepth, int maxFiles)
    {
        var found = 0;
        var foldersVisited = 0;
        const int MaxFolders = 1024;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && found < maxFiles && foldersVisited++ < MaxFolders)
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
            foreach (var child in folders.Take(Math.Max(0, MaxFolders - foldersVisited - queue.Count)))
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) queue.Enqueue((child, depth + 1));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static IReadOnlyList<SteamGameInstall> LoadInstalls(string steamPath)
    {
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

    internal static bool IsUnderPath(string candidate, string root)
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
