using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Eve.App.Services;

public sealed record GameDetection(
    string DisplayName,
    string ExeName,
    string WindowTitle,
    string WindowClass,
    nint WindowHandle,
    int ProcessId,
    bool IsDetected,
    bool IsForeground = false)
{
    public static GameDetection None { get; } = new("No game detected", string.Empty, string.Empty, string.Empty, 0, 0, false);
}

public sealed class ForegroundGameDetector
{
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe",
        "cmd.exe",
        "conhost.exe",
        "discord.exe",
        "discordcanary.exe",
        "dwm.exe",
        "epicgameslauncher.exe",
        "eve.exe",
        "explorer.exe",
        "firefox.exe",
        "gamebar.exe",
        "gamebarftserver.exe",
        "gamebarpresencewriter.exe",
        "msedge.exe",
        "medal.exe",
        "medalencoder.exe",
        "microsoft.photos.exe",
        "msiafterburner.exe",
        "notepad.exe",
        "nvidia app.exe",
        "nvidia overlay.exe",
        "nvidia share.exe",
        "nvidia web helper.exe",
        "nvcontainer.exe",
        "nvdisplay.container.exe",
        "obs64.exe",
        "overwolf.exe",
        "parsecd.exe",
        "photos.exe",
        "powershell.exe",
        "rtss.exe",
        "rtsshooksloader64.exe",
        "screenclippinghost.exe",
        "screensketch.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "snippingtool.exe",
        "spotify.exe",
        "steam.exe",
        "steamwebhelper.exe",
        "streamdeck.exe",
        "taskmgr.exe",
        "textinputhost.exe",
        "vlc.exe",
        "wmplayer.exe",
        "mpc-hc.exe",
        "mpc-hc64.exe",
        "windowsterminal.exe",
        "zen.exe"
    };

    private readonly Dictionary<string, string> _catalog = new(GameCatalog.BuiltIn, StringComparer.OrdinalIgnoreCase);

    // User-chosen exclusions on top of the built-in IgnoredExecutables list -
    // the built-ins can never cover every non-game app a machine runs, so the
    // user can exclude anything wrongly detected from the header's
    // detected-game flyout (or Settings > Game Detection). Swapped atomically
    // as a whole set since Detect() runs on a background thread.
    private volatile HashSet<string> _userIgnoredExecutables = new(StringComparer.OrdinalIgnoreCase);

    // Community-maintained additions to IgnoredExecutables, fetched from
    // GitHub (see RemoteGameExclusionsService) so a newly-discovered false
    // positive can be fixed for everyone by editing one file upstream instead
    // of shipping a new release. Starts with whatever was cached from the
    // last successful fetch (possibly empty on a first run) and gets swapped
    // in again if MainWindow's background refresh finds something newer -
    // same atomic-whole-set-swap pattern as _userIgnoredExecutables, for the
    // same reason (Detect() runs on a background thread).
    private volatile HashSet<string> _remoteIgnoredExecutables = new(StringComparer.OrdinalIgnoreCase);

    private GameDetection _lastGame = GameDetection.None;

    public void ApplyUserIgnoredExecutables(IEnumerable<string> executableNames)
    {
        _userIgnoredExecutables = new HashSet<string>(
            executableNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    public void ApplyRemoteIgnoredExecutables(IEnumerable<string> executableNames)
    {
        _remoteIgnoredExecutables = new HashSet<string>(
            executableNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    public ForegroundGameDetector()
    {
        LoadCatalog(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"));
        LoadCatalog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE", "game-catalog.json"));
        ApplyRemoteIgnoredExecutables(RemoteGameExclusionsService.LoadCached());
    }

    public GameDetection Detect()
    {
        var foreground = DetectForeground();
        if (foreground.IsDetected)
        {
            _lastGame = foreground with { IsForeground = true };
            return _lastGame;
        }

        // The sticky last-game path below never re-runs BuildDetection, so a
        // just-excluded exe would otherwise stay "detected" until its window
        // closed.
        if (_lastGame.IsDetected && (_userIgnoredExecutables.Contains(_lastGame.ExeName) || _remoteIgnoredExecutables.Contains(_lastGame.ExeName)))
        {
            _lastGame = GameDetection.None;
        }

        if (_lastGame.IsDetected && IsStillUsable(_lastGame))
        {
            _lastGame = _lastGame with { IsForeground = false };
            return _lastGame;
        }

        var runningGame = DetectRunningGame();
        _lastGame = runningGame.IsDetected ? runningGame with { IsForeground = false } : runningGame;
        return _lastGame;
    }

    public string DetectDisplayName() => Detect().DisplayName;

    // Lets a user-added game (settings -> Game Detection) get recognized without
    // needing a graphics-module check to pass - same catalog dictionary the
    // built-in list and game-catalog.json both feed into.
    public void ApplyCustomGameNames(IEnumerable<Eve.Core.Settings.GameCaptureOverride> overrides)
    {
        foreach (var entry in overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.ExecutableName) || string.IsNullOrWhiteSpace(entry.DisplayName)) continue;
            _catalog[entry.ExecutableName] = entry.DisplayName;
        }
    }

    private string _lastLoggedRejectedExe = string.Empty;

    private GameDetection DetectForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return GameDetection.None;
        var detection = BuildDetection(handle, out var exeName, out var reason);
        if (!detection.IsDetected && !string.IsNullOrEmpty(reason) && !string.Equals(exeName, _lastLoggedRejectedExe, StringComparison.OrdinalIgnoreCase))
        {
            _lastLoggedRejectedExe = exeName;
            AppLog.Debug($"Game detection: not detecting foreground window, exe={exeName}, reason={reason}.");
        }
        else if (detection.IsDetected)
        {
            _lastLoggedRejectedExe = string.Empty;
        }

        return detection;
    }

    private GameDetection DetectRunningGame()
    {
        var candidates = new List<GameDetection>();
        EnumWindows((handle, _) =>
        {
            var detection = BuildDetection(handle);
            if (detection.IsDetected) candidates.Add(detection);
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => _catalog.ContainsKey(candidate.ExeName))
            .ThenByDescending(candidate => WindowArea(candidate.WindowHandle))
            .FirstOrDefault() ?? GameDetection.None;
    }

    private GameDetection BuildDetection(IntPtr handle) => BuildDetection(handle, out _, out _);

    private GameDetection BuildDetection(IntPtr handle, out string exeName, out string rejectReason)
    {
        exeName = string.Empty;
        rejectReason = string.Empty;
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle)) return GameDetection.None;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return GameDetection.None;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            exeName = GetExecutableName(process);

            // Store/UWP games (Forza Horizon 6) put ApplicationFrameHost.exe's
            // frame window in the foreground; the game itself owns the
            // Windows.UI.Core.CoreWindow child. Re-target the process at the
            // hosted app so catalog matching sees the real exe, but keep the
            // frame host's top-level handle - that's the window capture wants.
            Process? hostedProcess = null;
            if (string.Equals(exeName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var hostedWindow = FindWindowEx(handle, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                GetWindowThreadProcessId(hostedWindow, out var hostedPid);
                if (hostedWindow != IntPtr.Zero && hostedPid != 0 && hostedPid != processId)
                {
                    processId = hostedPid;
                    hostedProcess = Process.GetProcessById((int)processId);
                    exeName = GetExecutableName(hostedProcess);
                }
            }
            using var hostedProcessLifetime = hostedProcess;

            if (string.IsNullOrWhiteSpace(exeName)) return GameDetection.None;
            if (IgnoredExecutables.Contains(exeName))
            {
                rejectReason = "on the ignored-executables list";
                return GameDetection.None;
            }
            if (_userIgnoredExecutables.Contains(exeName))
            {
                rejectReason = "excluded by the user";
                return GameDetection.None;
            }
            if (_remoteIgnoredExecutables.Contains(exeName))
            {
                rejectReason = "on the remote ignored-executables list";
                return GameDetection.None;
            }
            var isCatalogGame = _catalog.TryGetValue(exeName, out var catalogName) && !string.IsNullOrWhiteSpace(catalogName);

            var title = GetWindowTitle(handle);
            if (!isCatalogGame && string.IsNullOrWhiteSpace(title))
            {
                rejectReason = "no window title";
                return GameDetection.None;
            }
            if (IsTinyOrToolWindow(handle))
            {
                rejectReason = "window smaller than 320x240";
                return GameDetection.None;
            }
            if (!isCatalogGame && !HasGraphicsModule(hostedProcess ?? process))
            {
                rejectReason = "no D3D/DXGI/OpenGL/Vulkan module found (module enumeration may be blocked by anti-cheat) and not in the game catalog";
                return GameDetection.None;
            }
            // Catalog games are exempt from the overlay heuristics - a UWP
            // game's own window classes (ApplicationFrameWindow,
            // Windows.UI.Core.CoreWindow) are the same ones the heuristics
            // treat as overlay chrome.
            var className = GetWindowClass(handle);
            if (!isCatalogGame && IsOverlayWindow(title, className))
            {
                rejectReason = "looks like an overlay window (title/class matched an overlay pattern)";
                return GameDetection.None;
            }
            var displayName = isCatalogGame
                ? catalogName!
                : CleanExecutableName(exeName);
            return new GameDetection(displayName, exeName, title, className, handle, (int)processId, true);
        }
        catch (Exception error)
        {
            rejectReason = $"exception: {error.Message}";
            return GameDetection.None;
        }
    }

    private static readonly HashSet<string> GraphicsModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3d9.dll",
        "d3d10.dll",
        "d3d11.dll",
        "d3d12.dll",
        "dxgi.dll",
        "opengl32.dll",
        "vulkan-1.dll"
    };

    private static bool HasGraphicsModule(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (GraphicsModules.Contains(module.ModuleName)) return true;
                }
            }
        }
        catch
        {
            // Access denied (protected/anti-cheat process) - can't confirm, treat as not a game.
        }

        return false;
    }

    private static string GetExecutableName(Process process)
    {
        try
        {
            var fileName = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName)) return Path.GetFileName(fileName);
        }
        catch
        {
            // Some anti-cheat protected games block MainModule. ProcessName is still enough for catalog matching.
        }

        return string.IsNullOrWhiteSpace(process.ProcessName) ? string.Empty : process.ProcessName + ".exe";
    }

    private static bool IsStillUsable(GameDetection detection)
    {
        if (!detection.IsDetected || detection.WindowHandle == 0) return false;
        var handle = (IntPtr)detection.WindowHandle;
        return IsWindow(handle) && IsWindowVisible(handle) && !IsIconic(handle);
    }

    private void LoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var name = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) _catalog[property.Name] = name;
                }
                else if (property.Value.ValueKind == JsonValueKind.Object &&
                         property.Value.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) _catalog[property.Name] = name;
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"Game catalog load failed: {path}", error);
        }
    }

    private static string CleanExecutableName(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        foreach (var suffix in new[] { "-Win64-Shipping", "_Win64_Shipping", "Client-Win64-Shipping", "Shipping" })
        {
            name = name.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(name) ? exeName : name.Trim('-', '_', ' ');
    }

    private static long WindowArea(nint handle)
    {
        return GetWindowRect((IntPtr)handle, out var rect)
            ? Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top)
            : 0;
    }

    private static bool IsTinyOrToolWindow(IntPtr handle)
    {
        return !GetWindowRect(handle, out var rect) || rect.Right - rect.Left < 320 || rect.Bottom - rect.Top < 240;
    }

    private static bool IsOverlayWindow(string title, string className)
    {
        return ContainsAny(title,
                   "NVIDIA",
                   "GeForce",
                   "Game Bar",
                   "Xbox",
                   "Steam Overlay",
                   "Discord Overlay",
                   "Overwolf",
                   "Medal",
                   "overlay") ||
               ContainsAny(className,
                   "CEF",
                   "Chrome_WidgetWin",
                   "GameBar",
                   "Windows.UI.Core.CoreWindow",
                   "ApplicationFrameWindow");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private static string GetWindowClass(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        return GetClassName(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
