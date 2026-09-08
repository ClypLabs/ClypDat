namespace ClypDat.App.Services;

// One process-wide path registry. Reservations precede hydration; readers already
// in flight drain before the writer can encode or invalidate their caches.
internal static class SpotifyProcessingPaths
{
    private sealed class Entry
    {
        public bool Processing;
        public bool Failed;
        public bool Finished;
        public int Readers;
        public TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Ownership(string path)
    {
        public string Path { get; } = path;
        public bool Active = true;
    }
    private static readonly AsyncLocal<Ownership?> Owner = new();
    public static event Action<string>? Changed;
    internal static string Normalize(string path) => Path.GetFullPath(path);

    public static bool IsProcessing(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (Sync) return Entries.TryGetValue(Normalize(path), out var entry) && entry.Processing;
    }

    public static bool Failed(string path)
    {
        lock (Sync) return Entries.TryGetValue(Normalize(path), out var entry) && entry.Failed;
    }

    public static void Reserve(string path, bool retry = false)
    {
        path = Normalize(path);
        lock (Sync)
        {
            if (!Entries.TryGetValue(path, out var entry)) Entries[path] = entry = new();
            if (entry.Processing || (entry.Finished && !retry)) return;
            entry.Finished = false;
            entry.Processing = true;
            entry.Failed = false;
        }
        Changed?.Invoke(path);
    }

    public static void Finish(string path, SpotifyOverlayOutcome result)
    {
        path = Normalize(path);
        lock (Sync)
        {
            if (!Entries.TryGetValue(path, out var entry)) return;
            entry.Processing = false;
            entry.Finished = true;
            entry.Failed = result == SpotifyOverlayOutcome.Failed;

        }
        Changed?.Invoke(path);
    }

    public static IDisposable? TryRead(string path)
    {
        path = Normalize(path);
        if (Owner.Value is { Active: true } owner && string.Equals(owner.Path, path, StringComparison.OrdinalIgnoreCase)) return new Scope(() => { });
        lock (Sync)
        {
            if (!Entries.TryGetValue(path, out var entry)) Entries[path] = entry = new();
            if (entry.Processing) return null;
            if (entry.Readers++ == 0) entry.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Scope(() =>
            {
                lock (Sync)
                {
                    if (--entry.Readers != 0) return;
                    entry.Drained.TrySetResult();
                    if (!entry.Processing && !entry.Failed && !entry.Finished) Entries.Remove(path);
                }
            });
        }
    }

    public static Task DrainAsync(string path, CancellationToken token)
    {
        lock (Sync)
            return Entries.TryGetValue(Normalize(path), out var entry) && entry.Readers > 0
                ? entry.Drained.Task.WaitAsync(token) : Task.CompletedTask;
    }

    // Must be set synchronously in the writer's execution context.
    public static IDisposable Own(string path)
    {
        var previous = Owner.Value;
        var ownership = new Ownership(Normalize(path));
        Owner.Value = ownership;
        return new Scope(() => { ownership.Active = false; Owner.Value = previous; });
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed class SpotifyPostSaveCoordinator
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, Task<SpotifyOverlayOutcome>> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<SpotifyOverlayOutcome>> _saveIds = new(StringComparer.Ordinal);

    // Called on the UI thread. Retain outcomes so duplicate worker completions
    // cannot re-encode a failed save; only an explicit retry starts it again.
    public Task<SpotifyOverlayOutcome> RunAsync(string path, string? saveId, bool retry,
        Func<Task> releaseReaders, Func<CancellationToken, Task<SpotifyOverlayOutcome>> process,
        CancellationToken token = default)
    {
        path = SpotifyProcessingPaths.Normalize(path);
        if (_jobs.TryGetValue(path, out var existing) && (!retry || !existing.IsCompleted))
        {
            if (existing.IsCompletedSuccessfully) SpotifyProcessingPaths.Finish(path, existing.Result);
            return existing;
        }
        if (!retry && saveId is not null && _saveIds.TryGetValue(saveId, out existing))
        {
            SpotifyProcessingPaths.Finish(path, SpotifyOverlayOutcome.Skipped);
            return existing;
        }
        SpotifyProcessingPaths.Reserve(path, retry);
        var task = ExecuteAsync(path, releaseReaders, process, token);
        _jobs[path] = task;
        if (saveId is not null) _saveIds[saveId] = task;
        return task;
    }

    private async Task<SpotifyOverlayOutcome> ExecuteAsync(string path, Func<Task> releaseReaders,
        Func<CancellationToken, Task<SpotifyOverlayOutcome>> process, CancellationToken token)
    {
        await Task.Yield();
        var entered = false;
        var outcome = SpotifyOverlayOutcome.Failed;
        try
        {
            await _serial.WaitAsync(token);
            entered = true;
            await releaseReaders();
            await SpotifyProcessingPaths.DrainAsync(path, token);
            using var owner = SpotifyProcessingPaths.Own(path);
            outcome = await process(token);
        }
        catch (OperationCanceledException) { outcome = SpotifyOverlayOutcome.Cancelled; }
        catch (Exception error) { AppLog.Error($"Spotify post-save failed: {path}", error); }
        finally
        {
            SpotifyProcessingPaths.Finish(path, outcome);
            if (entered) _serial.Release();
        }
        return outcome;
    }
}
