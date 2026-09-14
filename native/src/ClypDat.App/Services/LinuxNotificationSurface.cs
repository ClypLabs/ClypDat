using System.Diagnostics;

namespace ClypDat.App.Services;

// Desktop notifications stay on KDE's notification service, outside the game.
internal sealed class LinuxNotificationSurface : IClipOverlaySurface
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;
    private uint _notification;
    private volatile bool _disposed;

    public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
    {
        Interlocked.Exchange(ref _generation, presentation.Generation);
        _ = PublishAsync(presentation, completion);
    }

    private async Task PublishAsync(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
    {
        var presented = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || presentation.Generation != Interlocked.Read(ref _generation)) return;
            var result = await RunAsync("/usr/bin/notify-send", ["--print-id", "--app-name=ClypDat",
                "--expire-time=4000", $"--replace-id={_notification}", "--", presentation.Event.Title,
                System.Security.SecurityElement.Escape(presentation.Event.Detail ?? string.Empty) ?? string.Empty]).ConfigureAwait(false);
            presented = uint.TryParse(result.Trim(), out _notification);
        }
        catch (Exception error) { AppLog.Error("Linux notification failed.", error); }
        finally
        {
            _gate.Release();
            completion(new(presentation.Generation, presented));
        }
    }

    public void Dismiss(long generation) => _ = DismissAsync(generation);
    private async Task DismissAsync(long generation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Interlocked.Read(ref _generation) || _notification == 0) return;
            await RunAsync("/usr/bin/busctl", ["--user", "call", "org.freedesktop.Notifications",
                "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "CloseNotification", "u",
                _notification.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ConfigureAwait(false);
            _notification = 0;
        }
        catch (Exception error) { AppLog.Error("Linux notification dismissal failed.", error); }
        finally { _gate.Release(); }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start desktop notification client.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Desktop notification service rejected the request.");
            return await output.ConfigureAwait(false);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }
    public void Dispose() { _disposed = true; Dismiss(Interlocked.Read(ref _generation)); }
}
