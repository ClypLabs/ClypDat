using Microsoft.Win32;

namespace Eve.App.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EVE";

    public static void SetLaunchOnStartup(bool enabled, bool minimized)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        var args = minimized ? " --minimized" : string.Empty;
        key.SetValue(ValueName, $"\"{exe}\"{args}");
    }
}
