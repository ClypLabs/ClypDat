using System.Diagnostics;

namespace ClypDat.App.Services;

public enum FortniteClipReplayState
{
    // No replay covers the clip: replays were off, or the match is too old.
    None,
    // The replay is still being written: the match has not ended.
    Waiting,
    // The replay was left unfinished (Fortnite closed mid-match).
    Incomplete,
    // A finished replay covers the clip.
    Ready
}

// Watches Fortnite's replay folder and turns finished replays into match
// summaries in the library (MatchStatsStore), one per replay, reused until
// the file changes. Read-only throughout: it never writes, moves or deletes a
// replay, never starts or stops recording, and creates no videos.
//
// Cost: the folder is listed and each new or changed replay's header (a few
// hundred bytes) read on a debounced watcher and a slow poll. Replays are
// decoded one at a time on a below-normal-priority thread, only when
// finished, and only for matches something in the library was recorded
// during or that the user opens. All decoding waits until no game is running.
internal sealed class FortniteReplayService : IDisposable
{
    private const string ProcessName = "FortniteClient-Win64-Shipping";
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    // A live replay untouched this long, with Fortnite closed, was abandoned.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(3);

    private readonly Func<MatchStatsStore?> _store;
    private readonly Func<bool> _isGameRunning;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTime, DateTime, bool> _hasFortniteMediaBetween;
    private readonly string _savedFolder;
    private readonly object _gate = new();
    private readonly Dictionary<string, FortniteReplayHeader> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queue = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _poll;
    private Thread? _worker;
    private bool _wasGameRunning;
    private bool _scannedOnce;
    private int _scanning;
    private long _lastBackfillAt;
    private int _filesFound;
    private int _abandonedFound;
    private int _disposed;
    private readonly long _startedAt = Environment.TickCount64;

    public FortniteReplayService(Func<MatchStatsStore?> store, Func<bool> isGameRunning, Func<bool> enabled,
        Func<DateTime, DateTime, bool> hasFortniteMediaBetween, string? folder = null, string? savedFolder = null)
    {
        _store = store;
        _isGameRunning = isGameRunning;
        _enabled = enabled;
        _hasFortniteMediaBetween = hasFortniteMediaBetween;
        Folder = folder ?? FortniteReplayFile.DefaultFolder;
        _savedFolder = savedFolder ?? FortniteAccounts.DefaultSavedFolder;
    }

    public string Folder { get; }

    /// <summary>The folder's replays changed: one appeared, grew, finished or went.</summary>
    public event EventHandler? Changed;

    /// <summary>A replay was decoded and saved. Raised on the worker thread.</summary>
    public event EventHandler<MatchStats>? MatchParsed;

    public bool FolderExists => Directory.Exists(Folder);
    public bool IsParsingDeferred => _isGameRunning();

    public int ReplayCount => Volatile.Read(ref _filesFound);

    public static bool IsFortniteRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    public void Start()
    {
        if (_worker is not null || _lifetime.IsCancellationRequested) return;
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Fortnite replays" };
        _worker.Start();
        _debounce = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(5), PollInterval);
    }

    private void Poll()
    {
        Rescan();
        // Retry after startup too: a large library can finish loading later,
        // and existing clips can be added without any replay file changing.
        var now = Environment.TickCount64;
        if (_enabled() && !_isGameRunning() && now - _startedAt > 30_000 &&
            (_lastBackfillAt == 0 || now - _lastBackfillAt > 180_000))
        {
            _lastBackfillAt = now;
            try { Backfill(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Info($"Fortnite replays: backfill skipped ({error.GetType().Name})."); }
        }
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null || !Directory.Exists(Folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(Folder, "*.replay") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Created += OnFolderEvent;
            watcher.Changed += OnFolderEvent;
            watcher.Deleted += OnFolderEvent;
            watcher.Renamed += OnFolderEvent;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Info($"Fortnite replays: folder not watched ({error.GetType().Name}).");
        }
    }

    // Fortnite rewrites a live replay every few seconds; settle first.
    private void OnFolderEvent(object sender, FileSystemEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;
        try { _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* A queued watcher notification raced shutdown. */ }
    }

    /// <summary>Lists the folder, reading headers only for new or changed files, and queues what has just finished.</summary>
    public void Rescan()
    {
        if (_lifetime.IsCancellationRequested || Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            EnsureWatcher();
            var files = Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.replay").Select(path => new FileInfo(path)).ToList()
                : [];
            var changed = Interlocked.Exchange(ref _filesFound, files.Count) != files.Count;
            var finished = new List<FortniteReplayHeader>();
            lock (_gate)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    seen.Add(file.FullName);
                    if (_headers.TryGetValue(file.FullName, out var known) && known.SizeBytes == file.Length && known.LastWriteUtc == file.LastWriteTimeUtc) continue;
                    var header = FortniteReplayFile.TryReadHeader(file.FullName);
                    if (header is null) continue;
                    _headers[file.FullName] = header;
                    changed = true;
                    // Finished since the last look. The first listing is left
                    // to Backfill, which checks the library once for all.
                    if (header.IsReady && (known?.IsLive ?? _scannedOnce)) finished.Add(header);
                }
                foreach (var gone in _headers.Keys.Where(path => !seen.Contains(path)).ToList())
                {
                    _headers.Remove(gone);
                    changed = true;
                }
                _scannedOnce = true;
            }
            // A match that ended while ClypDat was running: decode it now if
            // the library has something from it.
            foreach (var header in finished.Where(IsWanted)) Enqueue(header.Path, front: false);
            var gameRunning = _isGameRunning();
            var gameChanged = _wasGameRunning != gameRunning;
            if (_wasGameRunning && !gameRunning)
            {
                QueuePendingPlaylists();
                WakeWorker();
            }
            _wasGameRunning = gameRunning;
            // A stopped writer can become abandoned without any file change.
            if (!gameRunning) WakeWorker();
            // Only the moment one crosses into abandoned matters, not every poll
            // while a crashed match's live replay sits in the folder forever.
            var now = DateTime.UtcNow;
            var abandoned = gameRunning ? 0 : Headers().Count(header => !header.IsReady && now - header.LastWriteUtc > AbandonedAfter);
            var abandonedChanged = Interlocked.Exchange(ref _abandonedFound, abandoned) != abandoned;
            if (changed || gameChanged || abandonedChanged) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"Fortnite replays: folder not readable ({error.GetType().Name}).");
        }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    /// <summary>Queues every finished replay that something in the library was recorded during and that has no summary yet.</summary>
    public void Backfill()
    {
        if (!_enabled() || _store() is not { } store) return;
        var known = store.LoadAll().Where(match => match.Game == FortniteMatchStats.Game)
            .Select(match => match.SourceKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Headers().Where(h => h.IsReady && !known.Contains(h.CacheKey)).OrderByDescending(h => h.StartUtc))
            if (_hasFortniteMediaBetween(header.StartUtc, header.EndUtc)) Enqueue(header.Path, front: false);
        if (!_isGameRunning()) QueuePendingPlaylists();
        WakeWorker();
    }

    public IReadOnlyList<FortniteReplayHeader> Headers() { lock (_gate) return _headers.Values.ToList(); }

    /// <summary>The replay recorded during a span, finished or still being written; the save-time identity of a clip.</summary>
    public FortniteReplayHeader? ReplayAt(DateTime startUtc, DateTime endUtc)
    {
        Rescan();
        return Best(Headers(), startUtc, endUtc, DateTime.UtcNow);
    }

    internal static FortniteReplayHeader? Best(IEnumerable<FortniteReplayHeader> headers, DateTime startUtc, DateTime endUtc, DateTime nowUtc)
    {
        FortniteReplayHeader? best = null;
        var bestOverlap = TimeSpan.Zero;
        foreach (var header in headers)
        {
            // A live replay's length is not final: it runs to now.
            var end = header.IsLive ? Max(header.EndUtc, nowUtc) : header.EndUtc;
            var overlap = Min(end, endUtc) - Max(header.StartUtc, startUtc);
            if (overlap <= TimeSpan.Zero || overlap <= bestOverlap) continue;
            best = header;
            bestOverlap = overlap;
        }
        return best;
    }

    /// <summary>Where a clip's replay stands. A replay named at save time is preferred over time overlap.</summary>
    public (FortniteClipReplayState State, FortniteReplayHeader? Header) Resolve(ClipTimeline clip)
    {
        var headers = Headers();
        var header = clip.MatchSessionId is { } session
            ? headers.FirstOrDefault(h => string.Equals(h.FileName, session, StringComparison.OrdinalIgnoreCase))
            : null;
        if (clip.MatchSessionId is null) header ??= Best(headers, clip.StartUtc, clip.EndUtc, DateTime.UtcNow);
        if (header is null) return (FortniteClipReplayState.None, null);
        if (header.IsReady) return (FortniteClipReplayState.Ready, header);
        var stale = DateTime.UtcNow - header.LastWriteUtc > AbandonedAfter;
        return (stale && !IsFortniteRunning() ? FortniteClipReplayState.Incomplete : FortniteClipReplayState.Waiting, header);
    }

    /// <summary>The saved summary for this exact file, if it has been decoded.</summary>
    public MatchStats? Cached(FortniteReplayHeader header) =>
        _store()?.LoadAll().FirstOrDefault(match => match.Game == FortniteMatchStats.Game &&
                                                    string.Equals(match.SourceKey, header.CacheKey, StringComparison.OrdinalIgnoreCase));

    public bool HasFailed(FortniteReplayHeader header) { lock (_gate) return _failed.Contains(header.CacheKey); }

    /// <summary>Decode this replay next (the user is looking at a clip from it).</summary>
    public void Request(FortniteReplayHeader header) => Enqueue(header.Path, front: true);

    private bool IsWanted(FortniteReplayHeader header) => _enabled() && _hasFortniteMediaBetween(header.StartUtc, header.EndUtc);

    private void QueuePendingPlaylists()
    {
        if (!_enabled() || _store() is not { } store) return;
        var pending = store.LoadAll().Where(match => match.Game == FortniteMatchStats.Game && match.PlaylistPending)
            .Select(match => match.SourceKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Headers().Where(h => pending.Contains(h.CacheKey))) Enqueue(header.Path, front: false);
    }

    private void Enqueue(string path, bool front)
    {
        if (_lifetime.IsCancellationRequested) return;
        lock (_gate)
        {
            if (_queue.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                if (!front) return;
                _queue.Remove(_queue.First(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)));
            }
            if (front) _queue.AddFirst(path); else _queue.AddLast(path);
        }
        WakeWorker();
    }

    private void WakeWorker()
    {
        if (!_lifetime.IsCancellationRequested) _signal.Release();
    }

    private void Work()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { _signal.Wait(_lifetime.Token); }
            catch (OperationCanceledException) { return; }
            while (!_lifetime.IsCancellationRequested && _enabled())
            {
                // Keep queued work in place; the poll wakes it after play ends.
                if (_isGameRunning()) break;
                string? path;
                lock (_gate)
                {
                    path = _queue.First?.Value;
                    if (path is not null) _queue.RemoveFirst();
                }
                if (path is null) break;
                ParseOne(path);
            }
        }
    }

    private void ParseOne(string path)
    {
        var header = FortniteReplayFile.TryReadHeader(path);
        if (header is null || !header.IsReady || _store() is not { } store) return;
        if (_isGameRunning()) { Enqueue(path, front: true); return; }
        if (HasFailed(header)) return;
        var cached = Cached(header);
        var readPlaylist = !_isGameRunning();
        if (cached is not null && (!cached.PlaylistPending || !readPlaylist)) return;
        try
        {
            var clock = Stopwatch.StartNew();
            var data = FortniteReplayParser.Parse(header, readPlaylist);
            var owner = FortniteMatchStats.ResolveOwner(data, FortniteAccounts.LocalAccountIds(_savedFolder));
            var name = owner is null ? null : FortniteAccounts.DisplayName(_savedFolder, owner);
            var match = FortniteMatchStats.Build(data, owner, name) with { PlaylistPending = !readPlaylist };
            // A writer may still be completing the file; never cache a partial
            // parse under metadata that no longer describes its input.
            var after = FortniteReplayFile.TryReadHeader(path);
            if (after is null || !after.IsReady || after.CacheKey != header.CacheKey)
            {
                if (after?.IsReady == true) Enqueue(path, front: false);
                return;
            }
            if (_lifetime.IsCancellationRequested) return;
            store.Save(match);
            lock (_gate) _failed.Remove(header.CacheKey);
            AppLog.Info($"Fortnite replay read in {clock.ElapsedMilliseconds} ms: {data.Eliminations.Count} eliminations, " +
                        $"stats {(data.Stats is null ? "missing" : "found")}, playlist {(data.Playlist is null ? (readPlaylist ? "not found" : "deferred") : "found")}, " +
                        $"owner {(owner is null ? "unknown" : "found")}.");
            MatchParsed?.Invoke(this, match);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (_gate) _failed.Add(header.CacheKey);
            AppLog.Info($"Fortnite replay could not be read ({error.GetType().Name}: {error.Message}).");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _watcher?.Dispose();
        _debounce?.Dispose();
        _poll?.Dispose();
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
