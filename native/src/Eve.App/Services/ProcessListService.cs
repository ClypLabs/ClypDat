using System.Diagnostics;

namespace Eve.App.Services;

public static class ProcessListService
{
    public static IReadOnlyList<ProcessOption> GetOpenExecutables()
    {
        return Process.GetProcesses()
            .Select(GetProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new ProcessOption(name))
            .ToArray();
    }

    private static string GetProcessName(Process process)
    {
        var fallbackName = $"{process.ProcessName}.exe";
        try
        {
            var fileName = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return Path.GetFileName(fileName);
            }
        }
        catch
        {
            // Protected/system processes may deny module path access.
        }
        finally
        {
            process.Dispose();
        }

        return fallbackName;
    }
}
