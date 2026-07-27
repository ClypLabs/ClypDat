using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

public enum GameMatchSource { None, UserCustom, Catalog, Steam }

public sealed record GameDetection(
    string DisplayName,
    string ExeName,
    string WindowTitle,
    string WindowClass,
    nint WindowHandle,
    int ProcessId,
    bool IsDetected,
    bool IsForeground = false,
    GameMatchSource MatchSource = GameMatchSource.None)
{
    public static GameDetection None { get; } = new("No game detected", string.Empty, string.Empty, string.Empty, 0, 0, false);
}

public sealed class ForegroundGameDetector
{
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe", "cmd.exe", "conhost.exe", "discord.exe", "discordcanary.exe", "dwm.exe",
        "epicgameslauncher.exe", "clypdat.exe", "explorer.exe", "firefox.exe", "gamebar.exe", "gamebarftserver.exe",
        "gamebarpresencewriter.exe", "msedge.exe", "medal.exe", "medalencoder.exe", "microsoft.photos.exe",
        "msiafterburner.exe", "notepad.exe", "nvidia app.exe", "nvidia overlay.exe", "nvidia share.exe",
        "nvidia web helper.exe", "nvcontainer.exe", "nvdisplay.container.exe", "obs64.exe", "overwolf.exe",
        "parsecd.exe", "photos.exe", "powershell.exe", "rtss.exe", "rtsshooksloader64.exe", "screenclippinghost.exe",
        "screensketch.exe", "searchhost.exe", "shellexperiencehost.exe", "snippingtool.exe", "spotify.exe",
        "steam.exe", "steamwebhelper.exe", "streamdeck.exe", "taskmgr.exe", "textinputhost.exe", "vlc.exe",
        "wmplayer.exe", "mpc-hc.exe", "mpc-hc64.exe", "windowsterminal.exe", "zen.exe"
    };

    private readonly SteamGameLibrary _steamGames = new();
    private readonly ConcurrentDictionary<nint, CachedWindow> _windowCache = new();
    private volatile HashSet<string> _userIgnoredExecutables = new(StringComparer.OrdinalIgnoreCase);
    private volatile HashSet<string> _remoteIgnoredExecutables = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, string> _customGames = new(StringComparer.OrdinalIgnoreCase);
    private volatile CatalogState _catalog;
    private int _catalogGeneration;
    private GameDetection _lastGame = GameDetection.None;

    public ForegroundGameDetector()
    {
        var bundled = GameCatalog.BuiltIn.Select(pair => new GameCatalogEntry
        {
            Id = pair.Key,
            DisplayName = pair.Value,
            AntiCheatSensitive = GameCatalog.AntiCheatSensitive.Contains(pair.Key),
            Matchers = new List<GameWindowMatcher> { new() { Executable = pair.Key } }
        });
        var local = LoadRules(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"))
            .Concat(LoadRules(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "game-catalog.json")));
        _catalog = BuildCatalog(bundled.Concat(local).Concat(RemoteGameCatalogService.LoadCached()));
        ApplyRemoteIgnoredExecutables(RemoteGameExclusionsService.LoadCached());
    }

    public void ApplyUserIgnoredExecutables(IEnumerable<string> executableNames) =>
        _userIgnoredExecutables = new HashSet<string>(executableNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);

    public void ApplyRemoteIgnoredExecutables(IEnumerable<string> executableNames) =>
        _remoteIgnoredExecutables = new HashSet<string>(executableNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);

    public void ApplyRemoteCatalog(IEnumerable<GameCatalogEntry> entries)
    {
        var bundled = GameCatalog.BuiltIn.Select(pair => new GameCatalogEntry
        {
            Id = pair.Key, DisplayName = pair.Value,
            AntiCheatSensitive = GameCatalog.AntiCheatSensitive.Contains(pair.Key),
            Matchers = new List<GameWindowMatcher> { new() { Executable = pair.Key } }
        });
        var local = LoadRules(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"))
            .Concat(LoadRules(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "game-catalog.json")));
        _catalog = BuildCatalog(bundled.Concat(local).Concat(entries));
        Interlocked.Increment(ref _catalogGeneration);
        _windowCache.Clear();
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

        if (_lastGame.IsDetected && IsStillUsable(_lastGame) && !IsIgnored(_lastGame.ExeName))
        {
            _lastGame = _lastGame with { IsForeground = false };
            return _lastGame;
        }

        _lastGame = all.OrderByDescending(game => WindowArea(game.WindowHandle)).FirstOrDefault() ?? GameDetection.None;
        return _lastGame;
    }

    public string DetectDisplayName() => Detect().DisplayName;

    public IReadOnlyList<GameDetection> DetectAllRunningGames() => ScanWindows()
        .GroupBy(game => game.ExeName, StringComparer.OrdinalIgnoreCase)
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
            using var process = Process.GetProcessById((int)processId);
            var executablePath = GetExecutablePath(process);
            var exeName = Path.GetFileName(executablePath);
            if (string.IsNullOrWhiteSpace(exeName)) exeName = process.ProcessName + ".exe";
            if (string.Equals(exeName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var child = FindWindowEx(handle, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                GetWindowThreadProcessId(child, out var childPid);
                if (child != 0 && childPid != 0 && childPid != processId)
                {
                    using var hosted = Process.GetProcessById((int)childPid);
                    executablePath = GetExecutablePath(hosted);
                    exeName = Path.GetFileName(executablePath);
                    if (string.IsNullOrWhiteSpace(exeName)) exeName = hosted.ProcessName + ".exe";
                    processId = childPid;
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
                detection = Create(customName, exeName, title, className, handle, (int)processId, GameMatchSource.UserCustom);
            }
            else if (TryCatalogMatch(exeName, title, className, width, height, out var rule))
            {
                detection = Create(rule.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Catalog);
            }
            else if (_steamGames.FindByExecutablePath(executablePath) is { } steamGame)
            {
                detection = Create(steamGame.DisplayName, exeName, title, className, handle, (int)processId, GameMatchSource.Steam);
            }
            else detection = GameDetection.None;

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

    private static GameDetection Create(string displayName, string executable, string title, string className, nint handle, int processId, GameMatchSource source) =>
        new(displayName, executable, title, className, handle, processId, true, false, source);

    private bool IsIgnored(string executable) => IgnoredExecutables.Contains(executable) || _userIgnoredExecutables.Contains(executable) || _remoteIgnoredExecutables.Contains(executable);
    private static bool IsStillUsable(GameDetection detection) => detection.WindowHandle != 0 && IsWindow(detection.WindowHandle) && IsWindowVisible(detection.WindowHandle) && !IsIconic(detection.WindowHandle);
    private static long WindowArea(nint handle) => GetWindowRect(handle, out var rect) ? (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top) : 0;

    private static string GetExecutablePath(Process process)
    {
        try { if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName)) return process.MainModule.FileName; } catch { }
        var builder = new StringBuilder(32768);
        var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            var length = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref length) ? builder.ToString() : string.Empty;
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
}
