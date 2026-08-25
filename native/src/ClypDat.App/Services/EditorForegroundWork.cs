namespace ClypDat.App.Services;

/// <summary>
/// Marks the window where the user is waiting for a clip to start playing, so background
/// library work can stand aside for it.
///
/// The problem this solves: opening a clip during the initial library load left the
/// editor visible but silent for seconds. Two things compete with it. The cached-restore
/// loop realizes a card's visual tree on the UI thread every 32ms and runs three
/// whole-library filter passes per batch; and library hydration spawns ffprobe/ffmpeg
/// that share MediaProbeService's gate with the editor's own probe and waveform work,
/// while the editor separately needs chunk 0 of EVERY audio track before it can make a
/// sound. On a cold disk that is up to six ffmpeg processes racing the one the user is
/// actually waiting on.
///
/// Hydration is deliberately never cancelled when a clip is opened (see the comments in
/// MainWindowViewModel.OpenClipAsync) - a clip opened mid-load should not cost the rest
/// of the library its metadata. So this pauses the background producers instead, and
/// they resume the moment playback is up.
///
/// Shaped after ChunkedAudioReader's own priority counter, where lookahead extraction
/// parks while somebody is waiting on a chunk, so there is one idea here rather than two.
/// </summary>
public static class EditorForegroundWork
{
    private static int _active;

    /// <summary>True while an editor open is in flight. One volatile read when idle.</summary>
    public static bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>
    /// Opens a scope for the duration of an editor open. Dispose is idempotent, which is
    /// what lets the several paths that can end an open - success, supersede, teardown,
    /// error, and a timed backstop - all release it without coordinating.
    /// </summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope();
    }

    /// <summary>
    /// Waits while an editor open is in flight. Called by background producers at the
    /// point where they are about to do a unit of work, so the work never starts rather
    /// than being interrupted half way.
    /// </summary>
    public static async Task ParkWhileActiveAsync(CancellationToken cancellationToken)
    {
        while (IsActive)
        {
            // Long enough to be free, short enough to be invisible against work measured
            // in hundreds of milliseconds.
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Interlocked.Decrement(ref _active);
        }
    }
}
