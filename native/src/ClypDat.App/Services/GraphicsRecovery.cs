using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed class GraphicsDeviceUnavailableException(string message, GraphicsFailure failure) : InvalidOperationException(message)
{
    internal GraphicsFailure Failure { get; } = failure;
}

internal static class GraphicsRecovery
{
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan SaveDrainTimeout = TimeSpan.FromSeconds(30);
    internal static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(attempt switch { <= 0 => 0, 1 => 1, 2 => 2, 3 => 4, 4 => 8, _ => 10 });

    // GPU availability has no crash-loop budget. The owner's cancellation is
    // the only stop condition; every attempt still has a finite startup bound.
    internal static async Task RunAsync(Func<int, CancellationToken, Task<bool>> attempt, CancellationToken token,
        Action<int, TimeSpan>? waiting = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var index = 0; ; index++)
        {
            token.ThrowIfCancellationRequested();
            var backoff = Delay(index);
            waiting?.Invoke(index, backoff);
            if (backoff > TimeSpan.Zero) await delay(backoff, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(StartupTimeout);
            try {
                if (await attempt(index, deadline.Token).WaitAsync(StartupTimeout, token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (TimeoutException) { }
            catch (Exception error) when (error is not OperationCanceledException && error is not EditorGraphicsRestartRequiredException) { }
        }
    }
}
