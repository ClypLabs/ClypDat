namespace ClypDat.App.Services;

// A claimed player is safe to reuse only after its hover seek has finished
// and the native output has actually presented the landing picture.
internal sealed class EditorHoverWarmupReadiness
{
    private readonly TaskCompletionSource<bool> _seekCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Complete(bool succeeded) => _seekCompleted.TrySetResult(succeeded);

    internal async Task<bool> CanAdoptAsync(Task videoLoaded, Func<bool> playerAttached,
        Func<bool> nativeFramePresented, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        await videoLoaded.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!playerAttached()) return false;

        try
        {
            if (!await _seekCompleted.Task.WaitAsync(wait ?? TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
                return false;
        }
        catch (TimeoutException)
        {
            return false;
        }

        return playerAttached() && nativeFramePresented();
    }
}
