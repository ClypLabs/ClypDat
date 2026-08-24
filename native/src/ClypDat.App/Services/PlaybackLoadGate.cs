namespace ClypDat.App.Services;

// Serializes replacement of the shared LibVLC media/audio state. A reused
// PlaybackSession must never let two clip loads tear down and install media at
// the same time.
internal sealed class PlaybackLoadGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public IDisposable Enter(CancellationToken cancellationToken)
    {
        _semaphore.Wait(cancellationToken);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
