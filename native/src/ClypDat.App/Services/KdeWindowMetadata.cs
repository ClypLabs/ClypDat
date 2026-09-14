using System.Diagnostics;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed record KdeWindowMetadata(int Version, string Event, string Uuid, string AppId,
    string Title, int Pid, bool Focused, bool Minimized, int X, int Y, int Width, int Height);

// One metadata subprocess per app/worker. A disconnect atomically invalidates
// every old UUID, then a fresh watcher reacquires compositor identities.
internal sealed class KdeWindowMonitor : IDisposable
{
    private static readonly Lazy<KdeWindowMonitor> Instance = new(() => new());
    internal static KdeWindowMonitor Shared => Instance.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, KdeWindowMetadata> _windows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reader;
    private string _failure = "Waiting for KDE window metadata.";
    internal string Failure { get { lock (_gate) return _failure; } }
    internal KdeWindowMetadata[] Snapshot { get { lock (_gate) return _windows.Values.ToArray(); } }

    internal KdeWindowMonitor() => _reader = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var process = new Process { StartInfo = new(Path.Combine(AppContext.BaseDirectory, "libexec", "clypdat-gsr"))
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
                process.StartInfo.ArgumentList.Add("--clypdat-watch-windows");
                process.Start();
                using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                var errors = DrainErrorsAsync(process.StandardError, token);
                try {
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    if (line.Length > 65536) throw new InvalidDataException("KDE metadata exceeds the size limit.");
                    var window = JsonSerializer.Deserialize<KdeWindowMetadata>(line, JsonOptions);
                    if (window is null || window.Version != 1 || !Guid.TryParse(window.Uuid, out _))
                        throw new InvalidDataException("Invalid KDE metadata protocol.");
                    lock (_gate)
                    {
                        if (window.Event == "removed") _windows.Remove(window.Uuid);
                        else if (window.Event is "added" or "changed")
                        {
                            if (_windows.Count >= 4096 && !_windows.ContainsKey(window.Uuid)) throw new InvalidDataException("Too many KDE windows.");
                            _windows[window.Uuid] = window;
                        }
                        _failure = string.Empty;
                    }
                }
                }
                finally {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    try { await errors; } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                var reason = await errors;
                throw new IOException(string.IsNullOrWhiteSpace(reason) ? $"KDE metadata disconnected (exit {process.ExitCode})." : reason);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                lock (_gate) { _windows.Clear(); _failure = error.Message; }
                AppLog.Debug($"KDE metadata unavailable: {error.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { break; }
        }
        lock (_gate) _windows.Clear();
    }

    private static async Task<string> DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var last = string.Empty;
        while (await reader.ReadLineAsync(token) is { } line) last = line.Length > 4096 ? line[..4096] : line;
        return last;
    }

    public void Dispose() { _shutdown.Cancel(); }
}
