namespace ClypDat.App.Services;

// Tracks the top-level windows (dialogs, the shell file picker) that sit over
// the editor's video, so the floating hover bar stays down while any of them
// is up. Each cover is a token rather than a bare counter: a counter that
// missed one decrement - a dialog whose Show threw before its try/finally was
// entered - kept the bar hidden for the rest of the session with nothing in
// the log to say why. A token can be released twice harmlessly, names what
// took it, and can be reclaimed when the window it stood for is gone.
public sealed class EditorSurfaceCovers
{
    private readonly List<Cover> _live = new();
    private readonly Func<DateTime> _utcNow;

    public EditorSurfaceCovers(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsCovered => _live.Count > 0;

    // isOrphaned: asked by ReleaseOrphaned whether the thing this cover stands
    // for has gone away without releasing it (e.g. its window is no longer
    // visible). Leave null for covers whose release is guaranteed by scope.
    public IDisposable Acquire(string reason, Func<bool>? isOrphaned = null)
    {
        var cover = new Cover(this, reason, isOrphaned, _utcNow());
        _live.Add(cover);
        return cover;
    }

    public string Describe(bool withAge = true) => _live.Count == 0
        ? "none"
        : string.Join(", ", _live.Select(cover => withAge
            ? $"{cover.Reason} ({(_utcNow() - cover.AcquiredUtc).TotalSeconds:0}s)"
            : cover.Reason));

    // Releases covers older than minAge whose owner reports them orphaned and
    // returns their reasons, for the caller to log. The age floor keeps this
    // off a dialog that is still in the middle of being shown.
    public IReadOnlyList<string> ReleaseOrphaned(TimeSpan minAge)
    {
        if (_live.Count == 0) return Array.Empty<string>();
        var now = _utcNow();
        var released = new List<string>();
        foreach (var cover in _live.ToArray())
        {
            if (now - cover.AcquiredUtc < minAge || cover.IsOrphaned is null) continue;
            bool orphaned;
            try
            {
                orphaned = cover.IsOrphaned();
            }
            catch
            {
                // A window torn down far enough to throw on a property read
                // is as gone as it gets.
                orphaned = true;
            }
            if (!orphaned) continue;
            cover.Dispose();
            released.Add(cover.Reason);
        }
        return released;
    }

    private sealed class Cover(EditorSurfaceCovers owner, string reason, Func<bool>? isOrphaned, DateTime acquiredUtc) : IDisposable
    {
        private bool _released;

        public string Reason { get; } = reason;
        public Func<bool>? IsOrphaned { get; } = isOrphaned;
        public DateTime AcquiredUtc { get; } = acquiredUtc;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner._live.Remove(this);
        }
    }
}
