using Avalonia.Threading;

namespace ClypDat.App.Services;

// Process-wide registry of work that would be lost if ClypDat exited mid-way:
// saves, exports, trims, imports, file moves. The quit path reads it to decide
// whether to hold the exit behind the Closing Safely popup. Each operation owns
// a token; disposing it (normally via `using`) removes its label.
internal static class ShutdownGuard
{
    private sealed class Token(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null) End(entry);
        }
    }

    private sealed class Entry(string label)
    {
        public string Label { get; } = label;
    }

    private static readonly object Sync = new();
    private static readonly List<Entry> Active = new();
    private static readonly Dictionary<string, IDisposable> Keyed = new(StringComparer.Ordinal);
    private static TaskCompletionSource _idle = CompletedIdle();

    // Raised on the UI thread whenever the set of active operations changes.
    public static event Action? Changed;

    public static bool IsBusy
    {
        get { lock (Sync) return Active.Count > 0; }
    }

    // Distinct labels in start order, with a count suffix for repeats.
    public static IReadOnlyList<string> ActiveLabels
    {
        get
        {
            lock (Sync)
            {
                return Active
                    .GroupBy(entry => entry.Label)
                    .Select(group => group.Count() > 1 ? $"{group.Key} ({group.Count()})" : group.Key)
                    .ToList();
            }
        }
    }

    public static IDisposable Begin(string label)
    {
        var entry = new Entry(label);
        lock (Sync)
        {
            if (Active.Count == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Active.Add(entry);
        }
        RaiseChanged();
        return new Token(entry);
    }

    // For work whose start and end arrive as separate events (recorder saves,
    // full-session recording). Beginning an already-active key is a no-op.
    public static void BeginKeyed(string key, string label)
    {
        lock (Sync)
        {
            if (Keyed.ContainsKey(key)) return;
        }
        var token = Begin(label);
        lock (Sync)
        {
            if (Keyed.TryAdd(key, token)) return;
        }
        token.Dispose();
    }

    public static void EndKeyed(string key)
    {
        IDisposable? token;
        lock (Sync)
        {
            if (!Keyed.Remove(key, out token)) return;
        }
        token.Dispose();
    }

    public static Task WaitIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (Sync) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    private static void End(Entry entry)
    {
        TaskCompletionSource? completed = null;
        lock (Sync)
        {
            if (!Active.Remove(entry)) return;
            if (Active.Count == 0) completed = _idle;
        }
        completed?.TrySetResult();
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        if (Changed is null) return;
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
