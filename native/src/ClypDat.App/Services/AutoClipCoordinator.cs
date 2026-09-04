namespace ClypDat.App.Services;

internal enum AutoClipState
{
    NotInstalled, Downloading, WaitingForGame, Calibrating, Watching,
    Incompatible, Degraded, Recovering, Failed
}

internal sealed record AutoClipPolicy(
    Guid CaptureSessionId, string? ActiveGameId, bool Enabled,
    IReadOnlySet<string> EnabledEventIds, TimeSpan ReplayRetention);

internal sealed record AutoClipSignal(
    Guid CaptureSessionId, string ProviderId, string GameId, string EventId,
    string EventLabel, string OccurrenceId, double Confidence, DateTime TimestampUtc,
    TimeSpan Lead, TimeSpan Tail, int Priority = 0, string? PackId = null,
    string? PackVersion = null, string? PackHash = null);

internal sealed record AutoClipPlan(
    Guid Id, Guid CaptureSessionId, string ProviderId, string GameId,
    string DominantEventId, string DominantEventLabel, IReadOnlyList<string> EventIds,
    IReadOnlyList<string> OccurrenceIds, DateTime StartUtc, DateTime EndUtc, int Priority,
    string? PackId, string? PackVersion, string? PackHash);

internal interface IAutoClipCoordinator : IAsyncDisposable
{
    ValueTask ReconcileAsync(AutoClipPolicy policy, CancellationToken cancellationToken = default);
    ValueTask<bool> ObserveAsync(AutoClipSignal signal, CancellationToken cancellationToken = default);
}

internal sealed class AutoClipCoordinator : IAutoClipCoordinator
{
    internal const int Capacity = 32;
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(120);
    private readonly Func<AutoClipPlan, CancellationToken, Task> _save;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _gate = new();
    private readonly List<QueuedPlan> _queue = new();
    private readonly HashSet<string> _occurrences = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private AutoClipPolicy _policy = new(Guid.Empty, null, false, new HashSet<string>(), TimeSpan.Zero);
    private CancellationTokenSource _session = new();
    private long _arrival;
    private readonly Task _worker;

    public AutoClipCoordinator(Func<AutoClipPlan, CancellationToken, Task> save, Func<DateTime>? utcNow = null)
    {
        _save = save;
        _utcNow = utcNow ?? (() => MonotonicClock.UtcNow);
        _worker = Task.Run(ProcessAsync);
    }

    internal int PendingCount { get { lock (_gate) return _queue.Count; } }

    public ValueTask ReconcileAsync(AutoClipPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var sessionChanged = policy.CaptureSessionId != _policy.CaptureSessionId
                || !string.Equals(policy.ActiveGameId, _policy.ActiveGameId, StringComparison.OrdinalIgnoreCase);
            _policy = policy with { ReplayRetention = TimeSpan.FromSeconds(Math.Clamp(policy.ReplayRetention.TotalSeconds, 1, 1200)) };
            if (sessionChanged || !policy.Enabled)
            {
                _session.Cancel();
                _session.Dispose();
                _session = new CancellationTokenSource();
                _queue.Clear();
                _occurrences.Clear();
            }
        }
        SignalChanged();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ObserveAsync(AutoClipSignal signal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_policy.Enabled || signal.CaptureSessionId != _policy.CaptureSessionId
                || !string.Equals(signal.GameId, _policy.ActiveGameId, StringComparison.OrdinalIgnoreCase)
                || !_policy.EnabledEventIds.Contains(signal.EventId)
                || string.IsNullOrWhiteSpace(signal.OccurrenceId)) return ValueTask.FromResult(false);

            var occurrenceKey = $"{signal.ProviderId}\n{signal.GameId}\n{signal.OccurrenceId}";
            if (!_occurrences.Add(occurrenceKey)) return ValueTask.FromResult(false);

            var plan = CreatePlan(signal, _policy.ReplayRetention);
            var overlaps = _queue.Where(item => item.Plan.CaptureSessionId == plan.CaptureSessionId
                && string.Equals(item.Plan.GameId, plan.GameId, StringComparison.OrdinalIgnoreCase)
                && item.Plan.StartUtc <= plan.EndUtc && plan.StartUtc <= item.Plan.EndUtc).ToArray();
            if (overlaps.Length > 0)
            {
                foreach (var overlap in overlaps) _queue.Remove(overlap);
                plan = Merge(plan, overlaps.Select(item => item.Plan));
            }

            var queued = new QueuedPlan(plan, _arrival++);
            if (_queue.Count == Capacity)
            {
                var worst = _queue.OrderByDescending(item => item.Plan.StartUtc)
                    .ThenBy(item => item.Plan.Priority).ThenByDescending(item => item.Arrival).First();
                if (Compare(queued, worst) >= 0) return ValueTask.FromResult(false);
                _queue.Remove(worst);
            }
            _queue.Add(queued);
        }
        SignalChanged();
        return ValueTask.FromResult(true);
    }

    private AutoClipPlan CreatePlan(AutoClipSignal signal, TimeSpan retention)
    {
        var start = signal.TimestampUtc - signal.Lead;
        var end = signal.TimestampUtc + signal.Tail;
        var retainedStart = _utcNow() - retention;
        if (start < retainedStart) start = retainedStart;
        if (end - start > MaximumWindow) start = end - MaximumWindow;
        return new AutoClipPlan(Guid.NewGuid(), signal.CaptureSessionId, signal.ProviderId, signal.GameId,
            signal.EventId, signal.EventLabel, new[] { signal.EventId }, new[] { signal.OccurrenceId },
            start, end, signal.Priority, signal.PackId, signal.PackVersion, signal.PackHash);
    }

    private static AutoClipPlan Merge(AutoClipPlan incoming, IEnumerable<AutoClipPlan> existing)
    {
        var plans = existing.Append(incoming).ToArray();
        var dominant = plans.OrderByDescending(item => item.Priority).ThenBy(item => item.StartUtc).First();
        var end = plans.Max(item => item.EndUtc);
        var start = plans.Min(item => item.StartUtc);
        if (end - start > MaximumWindow) start = end - MaximumWindow;
        return dominant with
        {
            Id = Guid.NewGuid(),
            EventIds = plans.SelectMany(item => item.EventIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OccurrenceIds = plans.SelectMany(item => item.OccurrenceIds).Distinct(StringComparer.Ordinal).ToArray(),
            StartUtc = start, EndUtc = end, Priority = plans.Max(item => item.Priority)
        };
    }

    private async Task ProcessAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            QueuedPlan? next;
            CancellationToken sessionToken;
            lock (_gate)
            {
                next = _queue.OrderBy(item => item.Plan.StartUtc).ThenByDescending(item => item.Plan.Priority)
                    .ThenBy(item => item.Arrival).FirstOrDefault();
                sessionToken = _session.Token;
            }
            if (next is null)
            {
                await WaitForChangeAsync(_shutdown.Token).ConfigureAwait(false);
                continue;
            }

            var tail = next.Plan.EndUtc - _utcNow();
            if (tail > TimeSpan.Zero)
            {
                while (_changed.Wait(0)) { }
                try
                {
                    if (await _changed.WaitAsync(tail, sessionToken).ConfigureAwait(false)) continue;
                }
                catch (OperationCanceledException) { continue; }
            }

            lock (_gate)
            {
                if (!_queue.Remove(next) || next.Plan.CaptureSessionId != _policy.CaptureSessionId
                    || !string.Equals(next.Plan.GameId, _policy.ActiveGameId, StringComparison.OrdinalIgnoreCase)) continue;
            }
            try { await _save(next.Plan, sessionToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
            catch { /* One failed save must not stop later plans. */ }
        }
    }

    private async Task WaitForChangeAsync(CancellationToken cancellationToken)
    {
        try { await _changed.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void SignalChanged() { if (_changed.CurrentCount == 0) _changed.Release(); }

    private static int Compare(QueuedPlan left, QueuedPlan right)
    {
        var expiration = left.Plan.StartUtc.CompareTo(right.Plan.StartUtc);
        if (expiration != 0) return expiration;
        var priority = right.Plan.Priority.CompareTo(left.Plan.Priority);
        return priority != 0 ? priority : left.Arrival.CompareTo(right.Arrival);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_gate) _session.Cancel();
        SignalChanged();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _session.Dispose();
        _shutdown.Dispose();
        _changed.Dispose();
    }

    private sealed record QueuedPlan(AutoClipPlan Plan, long Arrival);
}
