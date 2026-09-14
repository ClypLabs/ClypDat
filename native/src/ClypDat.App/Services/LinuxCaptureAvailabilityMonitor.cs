using System.Diagnostics;

namespace ClypDat.App.Services;

// KDE lock state is queried conservatively. logind's suspend signal is handled
// independently, so capture stops before the display and PipeWire disappear.
internal sealed class LinuxCaptureAvailabilityMonitor : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _sleeping, _monitorReady;
    private bool _available;
    public event EventHandler<bool>? AvailabilityChanged;
    internal void Start() { _ = Task.Run(WatchSuspendAsync); _ = Task.Run(PollAsync); }
    private async Task PollAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            var available = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var state = await LinuxMediaProcess.RunAsync("busctl", ["--user", "call", "org.freedesktop.ScreenSaver",
                    "/ScreenSaver", "org.freedesktop.ScreenSaver", "GetActive"], timeout.Token);
                available = state.Trim() == "b false" && !_sleeping && _monitorReady;
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { AppLog.Debug($"KDE lock state unavailable: {error.Message}"); }
            Publish(available);
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
        }
        Publish(false);
    }
    private async Task WatchSuspendAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            using var process = new Process { StartInfo = new("dbus-monitor")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("--system");
            process.StartInfo.ArgumentList.Add("type='signal',interface='org.freedesktop.login1.Manager',member='PrepareForSleep'");
            try
            {
                process.Start();
                using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                var errors = process.StandardError.ReadToEndAsync(token);
                var sleepSignal = false;
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    if (line.StartsWith("signal ")) { _monitorReady = true; sleepSignal = line.Contains("member=PrepareForSleep", StringComparison.Ordinal); }
                    else if (sleepSignal && line.Trim().StartsWith("boolean ", StringComparison.Ordinal))
                    { _sleeping = line.Trim() == "boolean true"; sleepSignal = false; if (_sleeping) Publish(false); }
                }
                await process.WaitForExitAsync(token);
                AppLog.Debug("logind monitor disconnected: " + await errors);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { AppLog.Debug("logind monitor failed: " + error.Message); }
            finally { _monitorReady = false; Publish(false); }
            try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
        }
    }
    private void Publish(bool available)
    {
        lock (_shutdown)
        {
            if (_available == available) return;
            _available = available;
            AvailabilityChanged?.Invoke(this, available);
        }
    }
    public void Dispose() => _shutdown.Cancel();
}
