using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Eve.App.Services;

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
        "msedge.exe",
        "powershell.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "spotify.exe",
        "steam.exe",
        "taskmgr.exe",
        "textinputhost.exe",
        "windowsterminal.exe",
        "zen.exe"
    };

    private readonly Dictionary<string, string> _catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite"
    };

    public ForegroundGameDetector()
    {
        LoadCatalog(Path.Combine(AppContext.BaseDirectory, "game-catalog.json"));
        LoadCatalog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE", "game-catalog.json"));
    }

    public string DetectDisplayName()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return "No game detected";
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return "No game detected";

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName)) return "No game detected";
            var exeName = Path.GetFileName(fileName);
            if (IgnoredExecutables.Contains(exeName)) return "No game detected";

            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title) || IsTinyOrToolWindow(handle)) return "No game detected";
            if (_catalog.TryGetValue(exeName, out var displayName) && !string.IsNullOrWhiteSpace(displayName)) return displayName;
            return CleanExecutableName(exeName);
        }
        catch
        {
            return "No game detected";
        }
    }

    private void LoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (entries is null) return;
            foreach (var (exe, name) in entries)
            {
                if (!string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(name)) _catalog[exe] = name;
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

    private static bool IsTinyOrToolWindow(IntPtr handle)
    {
        return !GetWindowRect(handle, out var rect) || rect.Right - rect.Left < 320 || rect.Bottom - rect.Top < 240;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
