using System.Diagnostics;
using System.Text;

namespace Eve.App.Services;

public static class AppLog
{
    private static readonly object Lock = new();

    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE",
        "logs");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? error = null)
    {
        Write("ERROR", error is null ? message : $"{message}: {error}");
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(LogFolder);
        Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"eve-{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break capture/editor flow.
        }
    }
}
