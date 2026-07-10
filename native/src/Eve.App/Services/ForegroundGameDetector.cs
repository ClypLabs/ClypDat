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
    bool IsDetected)
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
        "eve.exe",
        "explorer.exe",
        "firefox.exe",
        "gamebar.exe",
        "gamebarftserver.exe",
        "gamebarpresencewriter.exe",
        "msedge.exe",
        "medal.exe",
        "medalencoder.exe",
        "msiafterburner.exe",
        "nvidia app.exe",
        "nvidia overlay.exe",
        "nvidia share.exe",
        "nvidia web helper.exe",
        "nvcontainer.exe",
        "nvdisplay.container.exe",
        "obs64.exe",
        "overwolf.exe",
        "powershell.exe",
        "rtss.exe",
        "rtsshooksloader64.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "spotify.exe",
        "steam.exe",
        "steamwebhelper.exe",
        "taskmgr.exe",
        "textinputhost.exe",
        "windowsterminal.exe",
        "zen.exe"
    };

    private readonly Dictionary<string, string> _catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteBootstrapper.exe"] = "Fortnite",
        ["FortniteLauncher.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping_EAC.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping_EAC_EOS.exe"] = "Fortnite",
        ["cs2.exe"] = "Counter-Strike 2"
    };

    private GameDetection _lastGame = GameDetection.None;

    public ForegroundGameDetector()
    {
        LoadCatalog(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"));
        LoadCatalog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE", "game-catalog.json"));
    }

    public GameDetection Detect()
    {
        var foreground = DetectForeground();
        if (foreground.IsDetected)
        {
            _lastGame = foreground;
            return foreground;
        }

        if (_lastGame.IsDetected && IsStillUsable(_lastGame)) return _lastGame;

        var runningGame = DetectRunningGame();
        _lastGame = runningGame;
        return runningGame;
    }

    public string DetectDisplayName() => Detect().DisplayName;

    private GameDetection DetectForeground()
    {
        var handle = GetForegroundWindow();
        return handle == IntPtr.Zero ? GameDetection.None : BuildDetection(handle);
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

    private GameDetection BuildDetection(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle)) return GameDetection.None;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return GameDetection.None;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var exeName = GetExecutableName(process);
            if (string.IsNullOrWhiteSpace(exeName)) return GameDetection.None;
            if (IgnoredExecutables.Contains(exeName)) return GameDetection.None;
            var isCatalogGame = _catalog.TryGetValue(exeName, out var catalogName) && !string.IsNullOrWhiteSpace(catalogName);

            var title = GetWindowTitle(handle);
            if ((!isCatalogGame && string.IsNullOrWhiteSpace(title)) || IsTinyOrToolWindow(handle)) return GameDetection.None;
            var className = GetWindowClass(handle);
            if (IsOverlayWindow(title, className)) return GameDetection.None;
            var displayName = isCatalogGame
                ? catalogName!
                : CleanExecutableName(exeName);
            return new GameDetection(displayName, exeName, title, className, handle, (int)processId, true);
        }
        catch
        {
            return GameDetection.None;
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
