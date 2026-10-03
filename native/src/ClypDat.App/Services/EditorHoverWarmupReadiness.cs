namespace ClypDat.App.Services;

// A claimed player is safe to reuse only after its hover seek has finished
// and the native output has actually presented the landing picture.
internal sealed class EditorHoverWarmupReadiness
{
    private readonly TaskCompletionSource<bool> _seekCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Complete(bool succeeded) => _seekCompleted.TrySetResult(succeeded);

    internal async Task<bool> CanAdoptAsync(Task videoLoaded, Func<bool> playerAttached,
        Func<bool> nativeFramePresented, CancellationToken cancellationToken, TimeSpan? wait = null,
        Action<string>? onFailure = null)
    {
        await videoLoaded.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!playerAttached())
        {
            onFailure?.Invoke("player-not-attached");
            return false;
        }

        bool seekSucceeded;
        try
        {
            seekSucceeded = await _seekCompleted.Task.WaitAsync(wait ?? TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            onFailure?.Invoke("seek-timeout");
            return false;
        }

        if (!seekSucceeded)
        {
            onFailure?.Invoke("seek-failed");
            return false;
        }

        if (!playerAttached())
        {
            onFailure?.Invoke("player-detached");
            return false;
        }

        if (!nativeFramePresented())
        {
            onFailure?.Invoke("native-frame-missing");
            return false;
        }

        return true;
    }
}
