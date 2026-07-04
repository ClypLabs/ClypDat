using System.Diagnostics;

namespace Eve.App.Services;

public static class ProcessListService
{
    public static IReadOnlyList<ProcessOption> GetOpenExecutables()
    {
        return Process.GetProcesses()
            .Select(GetProcess)
            .Where(process => process is not null)
            .Cast<ProcessOption>()
            .GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ProcessOption? GetProcess(Process process)
    {
        var fallbackName = $"{process.ProcessName}.exe";
        try
        {
            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (!IsUserFacingProcess(process, fileName)) return null;
            return new ProcessOption(Path.GetFileName(fileName), fileName);
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsUserFacingProcess(Process process, string fileName)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows) &&
            fileName.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
        {
            return true;
        }

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
}
