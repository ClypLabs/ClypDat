using System.Diagnostics;
using System.Text.Json;

namespace ClypDat.App.Services;

internal static class LinuxDesktopOutputs
{
    private static readonly object Gate = new();
    private static DesktopMonitorOption[] _snapshot = [];
    private static DateTime _nextRefresh;
    private static Task? _refresh;
    internal static IReadOnlyList<DesktopMonitorOption> Snapshot
    {
        get
        {
            lock (Gate)
            {
                if (DateTime.UtcNow >= _nextRefresh && _refresh is not { IsCompleted: false })
                {
                    _nextRefresh = DateTime.UtcNow.AddSeconds(5);
                    _refresh = Task.Run(RefreshAsync);
                }
                return _snapshot;
            }
        }
    }
    private static async Task RefreshAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new(Path.Combine(AppContext.BaseDirectory, "libexec", "clypdat-gsr"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        try
        {
            process.StartInfo.ArgumentList.Add("--clypdat-list-outputs");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException(await errors);
            var monitors = Parse(await output);
            lock (Gate) _snapshot = monitors;
        }
        catch (Exception error)
        {
            lock (Gate) _snapshot = [];
            AppLog.Debug($"Wayland outputs unavailable: {error.Message}");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }
    internal static DesktopMonitorOption[] Parse(string lines) => lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var e = document.RootElement;
            var name = e.GetProperty("name").GetString() ?? throw new InvalidDataException("Output has no name.");
            var width = e.GetProperty("width").GetInt32(); var height = e.GetProperty("height").GetInt32();
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || width <= 0 || height <= 0)
                throw new InvalidDataException("Invalid Wayland output.");
            return new DesktopMonitorOption(name, $"{name} — {width}×{height}", e.GetProperty("x").GetInt32(),
                e.GetProperty("y").GetInt32(), width, height, false);
        }).OrderBy(o => o.X).ThenBy(o => o.Y).ToArray();
}
