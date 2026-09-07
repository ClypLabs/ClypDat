using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

/// <summary>
/// Holds a detector game's events open for a moment so an escalating streak
/// becomes one clip at the tier it reached, instead of one clip per tier.
///
/// The GSI games already work this way - <see cref="LeagueAutoClipListener"/>
/// flushes after a quiet window, <see cref="Cs2GsiListener"/> upgrades its
/// pending label as kills land - and the detector games were the only ones
/// firing a save per event. That cost a tester ten queued saves in ninety
/// seconds, which tripped <see cref="StorageProtectionService"/>'s write-latency
/// rule and turned the tail of the burst into failures.
///
/// Which events combine is read from the catalog rather than hardcoded: events
/// sharing a <see cref="AutoClipEventDefinition.GroupId"/> are rungs of one
/// ladder and coalesce, and an event with no group is its own moment and fires
/// alone. That keeps Overwatch's Play of the Game - a post-match event with a
/// 15s lead - from swallowing a fight that happened seconds earlier.
/// </summary>
internal sealed class AutoClipEscalationBuffer : IDisposable
{
    /// <summary>
    /// Measured from a tester's clip containing a real ladder: DOUBLE KILL,
    /// TRIPLE KILL and QUADRUPLE KILL landed 1-3.5s apart across about seven
    /// seconds. Six seconds of quiet covers an escalation without gluing two
    /// separate fights into one clip.
    /// </summary>
    public static readonly TimeSpan DefaultQuietWindow = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan DefaultMaxWindow = TimeSpan.FromSeconds(20);

    private sealed record PendingGroup(DateTime FirstUtc, DateTime LastUtc, DateTime StartUtc, DateTime EndUtc,
        IReadOnlyList<AutoClipEvent> Events, int AnnouncedPriority);

    private readonly string _gameId;
    private readonly string _gameName;
    private readonly TimeSpan _quietWindow;
    private readonly TimeSpan _maxWindow;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _flushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoClipEventDefinition> _definitions;

    public AutoClipEscalationBuffer(string gameId, string gameName, IEnumerable<AutoClipEventDefinition> events,
        TimeSpan? quietWindow = null, TimeSpan? maxWindow = null)
    {
        _gameId = gameId;
        _gameName = gameName;
        _quietWindow = quietWindow ?? DefaultQuietWindow;
        _maxWindow = maxWindow ?? DefaultMaxWindow;
        _definitions = events.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Fired when a window opens or its tier improves, for the "clip started" toast.</summary>
    public event EventHandler<string>? Pending;

    /// <summary>Fired once per streak, with the window covering all of it.</summary>
    public event EventHandler<AutoClipRequest>? Ready;

    public void Offer(AutoClipDetectorEvent detected)
    {
        var definition = _definitions.GetValueOrDefault(detected.EventId);
        var item = new AutoClipEvent(detected.EventId, detected.EventLabel, detected.TimestampUtc, definition?.Priority ?? 0);

        // No group means nothing to escalate into: fire it on its own.
        if (definition?.GroupId is not { } group)
        {
            Pending?.Invoke(this, PendingMessage(detected.EventLabel));
            Ready?.Invoke(this, Build(new[] { item },
                detected.TimestampUtc - TimeSpan.FromSeconds(detected.LeadSeconds),
                detected.TimestampUtc + TimeSpan.FromSeconds(detected.TailSeconds)));
            return;
        }

        AutoClipRequest? capped = null;
        string? announce = null;
        lock (_gate)
        {
            if (_groups.TryGetValue(group, out var pending) && detected.TimestampUtc - pending.FirstUtc >= _maxWindow)
                capped = TakeLocked(group);

            var events = new List<AutoClipEvent>();
            var first = detected.TimestampUtc;
            var start = detected.TimestampUtc - TimeSpan.FromSeconds(detected.LeadSeconds);
            var announced = int.MinValue;
            if (_groups.TryGetValue(group, out var current))
            {
                events.AddRange(current.Events);
                first = current.FirstUtc;
                start = current.StartUtc;
                announced = current.AnnouncedPriority;
            }
            events.Add(item);

            // Announce only an improvement, the way CS2 only speaks when its
            // pending label changes - every rung otherwise re-toasts the same clip.
            if (item.Priority > announced)
            {
                announce = PendingMessage(detected.EventLabel);
                announced = item.Priority;
            }

            _groups[group] = new PendingGroup(first, detected.TimestampUtc, start,
                detected.TimestampUtc + TimeSpan.FromSeconds(detected.TailSeconds), events, announced);
            RestartFlushLocked(group);
        }

        if (announce is not null) Pending?.Invoke(this, announce);
        if (capped is not null) Ready?.Invoke(this, capped);
    }

    /// <summary>
    /// Drops everything without emitting. Used when the watched game changes, so
    /// a window opened in the last match cannot flush into the next one.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var flush in _flushes.Values)
            {
                flush.Cancel();
                flush.Dispose();
            }
            _flushes.Clear();
            _groups.Clear();
        }
    }

    private void RestartFlushLocked(string group)
    {
        if (_flushes.Remove(group, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
        var cts = new CancellationTokenSource();
        _flushes[group] = cts;
        _ = FlushAfterQuietAsync(group, cts.Token);
    }

    private async Task FlushAfterQuietAsync(string group, CancellationToken token)
    {
        try { await Task.Delay(_quietWindow, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        AutoClipRequest? request;
        lock (_gate)
        {
            if (token.IsCancellationRequested) return;
            request = TakeLocked(group);
        }
        if (request is not null) Ready?.Invoke(this, request);
    }

    private AutoClipRequest? TakeLocked(string group)
    {
        if (!_groups.Remove(group, out var pending)) return null;
        if (_flushes.Remove(group, out var flush))
        {
            flush.Cancel();
            flush.Dispose();
        }
        return Build(pending.Events, pending.StartUtc, pending.EndUtc);
    }

    /// <summary>
    /// The window spans the whole streak: the first event's lead through the last
    /// event's tail, so a Double Kill that became a Quadruple keeps the opening
    /// kill in frame.
    /// </summary>
    private AutoClipRequest Build(IReadOnlyList<AutoClipEvent> events, DateTime startUtc, DateTime endUtc)
    {
        var primary = events.OrderByDescending(item => item.Priority).ThenBy(item => item.OccurredUtc).First();
        return new AutoClipRequest(_gameId, _gameName, primary.Id, primary.Label,
            AutoClipTitleFormatter.Format(_gameId, events), startUtc, endUtc, primary.Priority, events.ToArray());
    }

    private static string PendingMessage(string label) => $"Auto clip started — {label} detected, finishing the clip.";

    public void Dispose() => Reset();
}
