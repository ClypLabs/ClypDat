using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

public enum GameMatchSource { None, UserCustom, Catalog, Steam, Epic, BattleNet, Riot }

public sealed record GameDetection(
    string DisplayName,
    string ExeName,
    string WindowTitle,
    string WindowClass,
    nint WindowHandle,
    int ProcessId,
    bool IsDetected,
    bool IsForeground = false,
    GameMatchSource MatchSource = GameMatchSource.None,
    string DetectionKey = "")
{
    public static GameDetection None { get; } = new("No game detected", string.Empty, string.Empty, string.Empty, 0, 0, false);
}

public sealed class ForegroundGameDetector
{
    private readonly SteamGameLibrary _steamGames = new();
    private readonly EpicGameLibrary _epicGames = new();
    private readonly BattleNetGameLibrary _battleNetGames = new();
    private readonly RiotGameLibrary _riotGames = new();
    private readonly ConcurrentDictionary<nint, CachedWindow> _windowCache = new();
    private volatile HashSet<string> _userIgnoredExecutables = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, string> _customGames = new(StringComparer.OrdinalIgnoreCase);
    private volatile CatalogState _catalog;
    private int _catalogGeneration;
    private GameDetection _lastGame = GameDetection.None;

    public ForegroundGameDetector()
    {
        var local = LoadRules(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"))
            .Concat(LoadRules(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "game-catalog.json")));
        _catalog = BuildCatalog(local.Concat(RemoteGameCatalogService.LoadCached()));
    }

    public void ApplyUserIgnoredExecutables(IEnumerable<string> executableNames) =>
        _userIgnoredExecutables = new HashSet<string>(executableNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);

    public void ApplyRemoteCatalog(IEnumerable<GameCatalogEntry> entries)
    {
        var local = LoadRules(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"))
            .Concat(LoadRules(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "game-catalog.json")));
        _catalog = BuildCatalog(local.Concat(entries));
        Interlocked.Increment(ref _catalogGeneration);
        _windowCache.Clear();
        _loggedUnmatched.Clear();
    }

    public void ApplyCustomGameNames(IEnumerable<ClypDat.Core.Settings.GameCaptureOverride> overrides)
    {
        // Existing settings entries with a display name predate Origin. They are
        // intentional user additions and stay recognized after strict matching lands.
        _customGames = overrides
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ExecutableName) && !string.IsNullOrWhiteSpace(entry.DisplayName) && entry.Origin != "Catalog")
            .GroupBy(entry => entry.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().DisplayName, StringComparer.OrdinalIgnoreCase);
        Interlocked.Increment(ref _catalogGeneration);
        _windowCache.Clear();
        _loggedUnmatched.Clear();
    }

    public GameDetection Detect()
    {
        var all = ScanWindows();
        var foregroundHandle = GetForegroundWindow();
        var foreground = all.FirstOrDefault(game => game.WindowHandle == foregroundHandle);
        if (foreground?.IsDetected == true)
        {
            _lastGame = foreground with { IsForeground = true };
            return _lastGame;
        }

        if (_lastGame.IsDetected && IsStillUsable(_lastGame) && !IsIgnored(_lastGame.ExeName) && !IsIgnored(_lastGame.DetectionKey))
        {
            _lastGame = PreferRealGameWindow(_lastGame, all) with { IsForeground = false };
            return _lastGame;
        }

        _lastGame = all.OrderByDescending(game => WindowArea(game.WindowHandle)).FirstOrDefault() ?? GameDetection.None;
        return _lastGame;
    }

    // Anti-cheat and platform launchers put a window up before the game's real
    // one exists, and catalog entries deliberately match them (see the bundled
    // Fortnite and Rainbow Six entries) so the replay buffer can start before
    // the match does. The cost is that _lastGame can latch onto that stub: it
    // stays "usable" for the whole session, it never comes to the foreground,
    // so the sticky branch above keeps handing it back and capture stays bound
    // to a window that is paused for as long as the game runs. Observed with
    // Rainbow Six Siege, where the BattlEye launcher's 704x299 window won and
    // the 2560x1440 game window was never captured.
    //
    // So: whenever a window for the SAME game is comfortably bigger than the
    // one currently held, that is the real game window - take it. Ratio rather
    // than an absolute floor, because a legitimately small windowed-mode game
    // must keep working; this only fires when the two differ by more than a
    // title bar's worth.
    private GameDetection PreferRealGameWindow(GameDetection current, IReadOnlyList<GameDetection> all)
    {
        var biggest = all
            .Where(game => IsSameGame(game, current))
            .OrderByDescending(game => WindowArea(game.WindowHandle))
            .FirstOrDefault();
        if (biggest is null || biggest.WindowHandle == current.WindowHandle) return current;
        return WindowArea(biggest.WindowHandle) > WindowArea(current.WindowHandle) * 2 ? biggest : current;
    }

    // DetectionKey groups a game's windows across however they matched (the
    // launcher can match on a different rule to the game itself). ExeName is
    // the fallback for a detection that never got a key.
    private static bool IsSameGame(GameDetection left, GameDetection right) =>
        !string.IsNullOrWhiteSpace(left.DetectionKey) && !string.IsNullOrWhiteSpace(right.DetectionKey)
            ? string.Equals(left.DetectionKey, right.DetectionKey, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.ExeName, right.ExeName, StringComparison.OrdinalIgnoreCase);

    public string DetectDisplayName() => Detect().DisplayName;

    public IReadOnlyList<GameDetection> DetectAllRunningGames() => ScanWindows()
        .GroupBy(game => string.IsNullOrWhiteSpace(game.DetectionKey) ? game.ExeName : game.DetectionKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(game => WindowArea(game.WindowHandle)).First())
        .ToArray();

    private IReadOnlyList<GameDetection> ScanWindows()
    {
        var seen = new HashSet<nint>();
        var results = new List<GameDetection>();
        EnumWindows((handle, _) =>
        {
            seen.Add(handle);
            var detection = BuildDetection(handle);
            if (detection.IsDetected) results.Add(detection);
            return true;
        }, IntPtr.Zero);
        foreach (var cached in _windowCache.Keys.Where(handle => !seen.Contains(handle))) _windowCache.TryRemove(cached, out _);
        // The window cache above is keyed by HWND and naturally stays small (one
        // entry per visible window). The PID->path cache has no such natural
        // eviction, so trim it to the PIDs actually seen this pass whenever it
        // grows past a small multiple of that - keeps long sessions from
        // accumulating an unbounded dictionary of exited processes.
        if (_processPathCache.Count > 256)
        {
            var livePids = new HashSet<int>(results.Select(r => r.ProcessId));
            foreach (var pid in _processPathCache.Keys.Where(pid => !livePids.Contains(pid))) _processPathCache.TryRemove(pid, out _);
        }
        return results;
    }

    private GameDetection BuildDetection(nint handle)
    {
        if (handle == 0 || !IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var rect)) return GameDetection.None;
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width == 0 || height == 0) return GameDetection.None;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return GameDetection.None;

        try
        {
            var executablePath = ResolveExecutablePath((int)processId);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                // OpenProcess/QueryFullProcessImageName failing is how a
                // protected (anti-cheat) or higher-integrity process presents,
                // and it used to be indistinguishable from "this window is not
                // a game" because neither logged anything. Keyed by PID since
                // there is no name to key by - that is the whole problem.
                LogUnmatchedOnce($"pid:{processId}", $"Game detection: could not read the executable path for pid {processId} - it is likely protected or running at a higher integrity level than ClypDat.");
                return GameDetection.None;
            }
            var exeName = Path.GetFileName(executablePath);
            if (string.Equals(exeName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var child = FindWindowEx(handle, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                GetWindowThreadProcessId(child, out var childPid);
                if (child != 0 && childPid != 0 && childPid != processId)
                {
                    var hostedPath = ResolveExecutablePath((int)childPid);
                    if (!string.IsNullOrWhiteSpace(hostedPath))
                    {
                        executablePath = hostedPath;
                        exeName = Path.GetFileName(executablePath);
                        processId = childPid;
                    }
                }
            }

            if (IsIgnored(exeName)) return GameDetection.None;
            var title = GetWindowTitle(handle);
            var className = GetWindowClass(handle);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(className)) return GameDetection.None;
            var signature = new WindowSignature((int)processId, exeName, executablePath, title, className, width, height, Volatile.Read(ref _catalogGeneration));
            if (_windowCache.TryGetValue(handle, out var cached) && cached.Signature == signature) return cached.Detection;

            GameDetection detection;
            if (_customGames.TryGetValue(exeName, out var customName))
            {
                detection = Create(customName, exeName, title, className, handle, (int)processId, GameMatchSource.UserCustom, exeName);
            }
            else if (TryCatalogMatch(exeName, title, className, width, height, out var rule))
            {
                detection = Create(rule.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Catalog, rule.Id);
            }
            else if (_steamGames.FindByExecutablePath(executablePath) is { } steamGame)
            {
                detection = Create(steamGame.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Steam, $"steam-{steamGame.AppId}");
            }
            else if (_epicGames.FindByExecutablePath(executablePath) is { } epicGame)
            {
                detection = Create(epicGame.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Epic, $"epic-{Normalize(epicGame.DisplayName)}");
            }
            else if (_battleNetGames.FindByExecutablePath(executablePath) is { } battleNetGame)
            {
                detection = Create(battleNetGame.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.BattleNet, $"battlenet-{Normalize(battleNetGame.DisplayName)}");
            }
            else if (_riotGames.FindByExecutablePath(executablePath) is { } riotGame)
            {
                detection = Create(riotGame.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Riot, $"riot-{Normalize(riotGame.DisplayName)}");
            }
            // Last resort: the executable is not under any library folder, but
            // a game does own that filename. Reported exactly as a path match
            // would be - same source, same steam-{AppId} key - so a game found
            // this way cannot produce a second Game Detection row for itself.
            else if (_steamGames.FindByExecutableName(exeName) is { } steamGameByName)
            {
                detection = Create(steamGameByName.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Steam, $"steam-{steamGameByName.AppId}");
            }
            else
            {
                detection = GameDetection.None;
                LogUnmatchedOnce(exeName, $"Game detection: no match for {exeName} (path={executablePath}, title='{title}', class={className}).");
            }

            if (detection.IsDetected && IsIgnored(detection.DetectionKey)) detection = GameDetection.None;

            _windowCache[handle] = new CachedWindow(signature, detection);
            return detection;
        }
        catch (Exception error)
        {
            AppLog.Debug($"Game detection: skipped window {handle}, reason={error.Message}.");
            return GameDetection.None;
        }
    }

    private bool TryCatalogMatch(string executable, string title, string className, int width, int height, out GameCatalogEntry matched)
    {
        foreach (var entry in _catalog.Entries)
        {
            if (entry.BlockedWindows.Any(block => GameCatalogRules.Matches(block, executable, title, className, width, height)))
            {
                matched = null!;
                return false;
            }
            if (entry.Matchers.Any(matcher => GameCatalogRules.Matches(matcher, executable, title, className, width, height)))
            {
                matched = entry;
                return true;
            }
        }
        matched = null!;
        return false;
    }

    private static GameDetection Create(string displayName, string executable, string title, string className, nint handle, int processId, GameMatchSource source, string detectionKey) =>
        new(displayName, executable, title, className, handle, processId, true, false, source, detectionKey);

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // This poll runs over every visible window every 1-3s, so an un-deduped
    // line here would bury the debug log within a minute (the same trap the
    // editor hover bar's own logging calls out). One line per distinct subject
    // per catalog generation is enough to answer "why was my game not picked
    // up" without drowning everything else.
    private readonly ConcurrentDictionary<string, byte> _loggedUnmatched = new();

    private void LogUnmatchedOnce(string key, string message)
    {
        if (!_loggedUnmatched.TryAdd(key, 0)) return;
        AppLog.Debug(message);
    }

    private bool IsIgnored(string executable) => _userIgnoredExecutables.Contains(executable);
    private static bool IsStillUsable(GameDetection detection) => detection.WindowHandle != 0 && IsWindow(detection.WindowHandle) && IsWindowVisible(detection.WindowHandle) && !IsIconic(detection.WindowHandle);
    private static long WindowArea(nint handle) => GetWindowRect(handle, out var rect) ? (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top) : 0;

    // Process.GetProcessById + MainModule used to sit here. On Windows,
    // Process.GetProcessById validates its PID via NtQuerySystemInformation,
    // which snapshots every process AND thread on the machine, and MainModule
    // enumerates the target's module list (and throws for elevated/protected
    // processes, so the old fallback below ran the work twice). Called once per
    // visible top-level window on a 1s timer, that was dozens of full
    // process-table snapshots per second - worst right at logon, against the
    // coldest page cache, which is exactly when this was reported to lock up
    // the whole PC. QueryFullProcessImageName on a handle from the window's own
    // PID needs none of that. The creation-time cache below skips even this
    // lightweight call on repeat ticks for a window that hasn't changed
    // process, while still detecting PID reuse.
    private readonly ConcurrentDictionary<int, (string ExecutablePath, long CreationTimeTicks)> _processPathCache = new();

    private string ResolveExecutablePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            long creationTicks = 0;
            if (GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                creationTicks = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            }

            if (_processPathCache.TryGetValue(processId, out var cached) && cached.CreationTimeTicks == creationTicks)
            {
                return cached.ExecutablePath;
            }

            var builder = new StringBuilder(32768);
            var length = builder.Capacity;
            var path = QueryFullProcessImageName(handle, 0, builder, ref length) ? builder.ToString() : string.Empty;
            _processPathCache[processId] = (path, creationTicks);
            return path;
        }
        finally { CloseHandle(handle); }
    }

    private static string GetWindowTitle(nint handle) { var length = GetWindowTextLength(handle); var builder = new StringBuilder(Math.Max(1, length + 1)); return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty; }
    private static string GetWindowClass(nint handle) { var builder = new StringBuilder(256); return GetClassName(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty; }
    private static IReadOnlyList<GameCatalogEntry> LoadRules(string path) { try { return File.Exists(path) ? GameCatalogRules.Parse(File.ReadAllText(path)) : Array.Empty<GameCatalogEntry>(); } catch (Exception error) { AppLog.Error($"Game catalog load failed: {path}", error); return Array.Empty<GameCatalogEntry>(); } }
    private static CatalogState BuildCatalog(IEnumerable<GameCatalogEntry> entries) => new(entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray());

    private sealed record CatalogState(IReadOnlyList<GameCatalogEntry> Entries);
    private sealed record CachedWindow(WindowSignature Signature, GameDetection Detection);
    private sealed record WindowSignature(int ProcessId, string Executable, string ExecutablePath, string Title, string ClassName, int Width, int Height, int Generation);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private readonly struct Rect { public readonly int Left; public readonly int Top; public readonly int Right; public readonly int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);
}
