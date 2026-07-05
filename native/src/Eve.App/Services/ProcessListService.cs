using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Eve.App.Services;

public static class ProcessListService
{
    public static IReadOnlyList<ProcessOption> GetOpenExecutables()
    {
        var windows = new List<ProcessOption>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title)) return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                var option = GetProcess(process, title);
                if (option is not null) windows.Add(option);
            }
            catch
            {
                // Windows can disappear while enumerating.
            }

            return true;
        }, IntPtr.Zero);

        return windows
            .GroupBy(process => $"{process.Name}|{process.WindowTitle}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(process => process.WindowTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ProcessOption? GetProcess(Process process, string windowTitle)
    {
        try
        {
            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (!IsUserFacingProcess(fileName, windowTitle)) return null;
            return new ProcessOption(Path.GetFileName(fileName), fileName, windowTitle);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUserFacingProcess(string fileName, string windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(windowTitle)) return true;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { local, roaming, programs, programsX86 })
        {
            if (!string.IsNullOrWhiteSpace(root) &&
                fileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
