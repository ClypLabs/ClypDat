using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>One window owns one LibVLC session for its entire lifetime.</summary>
internal sealed class PlaybackSessionOwner : IDisposable
{
    private readonly object _gate = new();
    private Task<PlaybackSession>? _creation;
    private bool _disposed;

    public Task<PlaybackSession> GetAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _creation ??= Task.Run(Create);
        }
    }

    private PlaybackSession Create()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            AppLog.Info("Playback preparation: Core.Initialize starting.");
            var session = new PlaybackSession();
            AppLog.Info($"Playback preparation: LibVLC and MediaPlayer ready in {clock.ElapsedMilliseconds}ms.");
            lock (_gate)
            {
                if (!_disposed) return session;
            }
            session.Dispose();
            throw new ObjectDisposedException(nameof(PlaybackSessionOwner));
        }
        catch (Exception error)
        {
            AppLog.Error($"Playback preparation failed after {clock.ElapsedMilliseconds}ms.", error);
            throw;
        }
    }

    public void Dispose()
    {
        Task<PlaybackSession>? creation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            creation = _creation;
        }
        if (creation is null) return;
        _ = creation.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
        }, TaskScheduler.Default);
    }
}
