using Avalonia;

namespace ClypDat.App.Services;

internal enum ClipOverlayKind
{
    Saving,
    Saved,
    Failure,
    Recording,
    GameStarted,
    AutoClip,
    Standalone
}

internal enum ClipOverlayPlacement
{
    TopLeft,
    TopRight,
    CenterLeft,
    CenterRight,
    BottomLeft,
    BottomRight
}

internal static class ClipOverlayPlacementParser
{
    public static ClipOverlayPlacement Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TOP LEFT" => ClipOverlayPlacement.TopLeft,
        "CENTER LEFT" => ClipOverlayPlacement.CenterLeft,
        "CENTER RIGHT" => ClipOverlayPlacement.CenterRight,
        "BOTTOM LEFT" => ClipOverlayPlacement.BottomLeft,
        "BOTTOM RIGHT" => ClipOverlayPlacement.BottomRight,
        _ => ClipOverlayPlacement.TopRight
    };
}

internal sealed record ClipOverlayEvent(
    Guid WorkflowId,
    int Stage,
    DateTime RequestedUtc,
    DateTime OccurredUtc,
    int Priority,
    ClipOverlayKind Kind,
    string Title,
    string? Detail,
    ClipOverlayTarget Target,
    ClipOverlayPlacement Placement,
    bool ExcludeFromCapture,
    bool IsRecovered = false,
    bool ShowVisual = true,
    string? Hotkey = null,
    string? HotkeyHint = null,
    string? SoundVolume = null);

internal sealed record ClipOverlayPresentation(
    long Generation,
    ClipOverlayEvent Event);

// What the surface did to get one presentation on screen: where the time
// went, where the window landed, and whether it could be confirmed there.
internal sealed record ClipOverlayPresentationReport(
    string Backend,
    string Monitor,
    double QueueMs,
    double RasterMs,
    double PostMs,
    double PresentMs,
    double VerifyMs,
    double TotalMs,
    string Affinity,
    string Recovery,
    bool Confirmed,
    string? Note = null);

// Presented is true only once the surface saw the window on screen. Reason
// says why not, otherwise.
internal readonly record struct ClipOverlayPresentationResult(
    long Generation,
    bool Presented,
    string? Reason = null,
    ClipOverlayPresentationReport? Report = null);

internal interface IClipOverlaySurface : IDisposable
{
    // Completion is called exactly once for every presentation.
    void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion);
    void Dismiss(long generation);
}

internal interface IClipOverlayScheduler : IDisposable
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class ClipOverlayScheduler : IClipOverlayScheduler
{
    private bool _disposed;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        if (_disposed) return EmptyDisposable.Instance;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            callback();
        }, null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    public void Dispose() => _disposed = true;

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}

// Cumulative notification outcomes, for the log and for tests.
internal sealed class ClipOverlayCounters
{
    public static ClipOverlayCounters Shared { get; } = new();
    private long _requested, _admitted, _presented, _unconfirmed, _skipped, _queued, _failed, _timedOut;
    private long _dcompRebuilds, _layeredFallbacks, _topmostRecoveries, _retargets, _windowRecreations, _verificationRecoveries;
    public long Requested => Volatile.Read(ref _requested);
    public long Admitted => Volatile.Read(ref _admitted);
    public long Presented => Volatile.Read(ref _presented);
    public long Unconfirmed => Volatile.Read(ref _unconfirmed);
    public long Skipped => Volatile.Read(ref _skipped);
    public long Queued => Volatile.Read(ref _queued);
    public long Failed => Volatile.Read(ref _failed);
    public long TimedOut => Volatile.Read(ref _timedOut);
    public long DirectCompositionRebuilds => Volatile.Read(ref _dcompRebuilds);
    public long LayeredFallbacks => Volatile.Read(ref _layeredFallbacks);
    public long TopmostRecoveries => Volatile.Read(ref _topmostRecoveries);
    public long Retargets => Volatile.Read(ref _retargets);
    public long WindowRecreations => Volatile.Read(ref _windowRecreations);
    public long VerificationRecoveries => Volatile.Read(ref _verificationRecoveries);
    internal void Request() => Interlocked.Increment(ref _requested);
    internal void Admit() => Interlocked.Increment(ref _admitted);
    internal void Present(bool confirmed) { if (confirmed) Interlocked.Increment(ref _presented); else Interlocked.Increment(ref _unconfirmed); }
    internal void Skip() => Interlocked.Increment(ref _skipped);
    internal void Queue() => Interlocked.Increment(ref _queued);
    internal void Fail(bool timedOut) { Interlocked.Increment(ref _failed); if (timedOut) Interlocked.Increment(ref _timedOut); }
    internal void DirectCompositionRebuilt() => Interlocked.Increment(ref _dcompRebuilds);
    internal void LayeredFallback() => Interlocked.Increment(ref _layeredFallbacks);
    internal void TopmostRecovered() => Interlocked.Increment(ref _topmostRecoveries);
    internal void Retargeted() => Interlocked.Increment(ref _retargets);
    internal void WindowRecreated() => Interlocked.Increment(ref _windowRecreations);
    internal void VerificationRecovered() => Interlocked.Increment(ref _verificationRecoveries);
    public string Summary =>
        $"requested={Requested} admitted={Admitted} presented={Presented} unconfirmed={Unconfirmed} skipped={Skipped} queued={Queued} " +
        $"failed={Failed} timedOut={TimedOut} dcompRebuilds={DirectCompositionRebuilds} layeredFallbacks={LayeredFallbacks} " +
        $"topmostRecoveries={TopmostRecoveries} verificationRecoveries={VerificationRecoveries} retargets={Retargets} windowRecreations={WindowRecreations}";
}

// Owns notification policy. Producers publish facts; coordinator alone decides
// what can replace the one native surface and when that surface leaves.
//
// Every notification ends in exactly one logged outcome: presented (seen on
// screen), unconfirmed (on screen as far as Windows reports, but possibly
// under an exclusive-fullscreen game), skipped (with the policy reason) or
// failed (with the surface's reason). A notification that loses the surface to
// a more important one waits in a small queue instead of vanishing.
internal sealed class ClipOverlayCoordinator : IDisposable
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GameStartedDwell = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PresentationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumEventAge = TimeSpan.FromSeconds(30);
    // How long a notification may wait for the surface. Results stay worth
    // showing for longer than progress and hints do.
    private static readonly TimeSpan ResultPendingLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OtherPendingLifetime = TimeSpan.FromSeconds(5);
    internal const int PendingCapacity = 3;
    private const int SoundHistoryLimit = 512;

    private readonly object _gate = new();
    private readonly IClipOverlaySurface _surface;
    private readonly IClipOverlayScheduler _scheduler;
    private readonly Action<string> _playSuccessSound;
    private readonly Func<DateTime> _utcNow;
    private readonly ClipOverlayCounters _counters;
    private readonly Action<string, bool> _log;
    private readonly Dictionary<Guid, int> _workflowStages = new();
    private readonly Queue<Guid> _workflowStageOrder = new();
    private readonly HashSet<Guid> _soundedSaves = new();
    private readonly Queue<Guid> _soundOrder = new();
    private readonly List<Entry> _pending = new();
    // Published and waiting for the surface's completion, by generation.
    private readonly Dictionary<long, Entry> _inFlight = new();
    private readonly Dictionary<long, IDisposable> _timeouts = new();
    private Entry? _visible;
    private IDisposable? _dismissal;
    private readonly Action<Action> _dispatchSound;
    private long _generation, _sequence;
    private bool _disposed;

    public ClipOverlayCoordinator(
        IClipOverlaySurface surface,
        IClipOverlayScheduler scheduler,
        Action<string> playSuccessSound,
        Func<DateTime>? utcNow = null,
        Action<Action>? dispatchSound = null,
        ClipOverlayCounters? counters = null,
        Action<string, bool>? log = null)
    {
        // Outcome lines to the app log (debug lines to the debug log) unless
        // a test hands in its own sink.
        _log = log ?? ((message, debug) => { if (debug) AppLog.Debug(message); else AppLog.Info(message); });
        _surface = surface;
        _scheduler = scheduler;
        _playSuccessSound = playSuccessSound;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _counters = counters ?? ClipOverlayCounters.Shared;
        // Off the caller thread by default; tests hand in an inline dispatcher
        // so a chime is observable the moment Publish returns.
        _dispatchSound = dispatchSound ?? (play => ThreadPool.QueueUserWorkItem(_ => play()));
    }

    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    public void Publish(ClipOverlayEvent notification)
    {
        string? soundVolume = null;
        var effects = new Effects();
        lock (_gate)
        {
            _counters.Request();
            if (_disposed) Skip(effects, notification, "coordinator-disposed");
            else
            {
                // Sound belongs to save identity, not visual admission. A lower
                // priority success may lose the surface but must still sound once.
                if (!notification.IsRecovered && notification.Kind == ClipOverlayKind.Saved && _soundedSaves.Add(notification.WorkflowId))
                {
                    _soundOrder.Enqueue(notification.WorkflowId);
                    while (_soundOrder.Count > SoundHistoryLimit) _soundedSaves.Remove(_soundOrder.Dequeue());
                    soundVolume = notification.SoundVolume;
                }
                var now = _utcNow();
                var rejection = notification.IsRecovered ? "recovered-event"
                    : now - notification.OccurredUtc > MaximumEventAge ? "stale-event"
                    : !notification.ShowVisual ? "visual-disabled"
                    : _workflowStages.TryGetValue(notification.WorkflowId, out var lastStage) && notification.Stage <= lastStage
                        ? notification.Stage == lastStage ? "duplicate-workflow-stage" : "old-workflow-stage"
                        : null;
                if (rejection is not null) Skip(effects, notification, rejection);
                else
                {
                    RememberStage(notification);
                    var entry = new Entry(notification, ++_sequence, now);
                    // A newer stage of a waiting workflow takes its place in line:
                    // a Saving that never got the surface becomes its Saved.
                    var waiting = _pending.FindIndex(item => item.Event.WorkflowId == notification.WorkflowId);
                    if (waiting >= 0)
                    {
                        Skip(effects, _pending[waiting].Event, "superseded-by-stage");
                        _pending.RemoveAt(waiting);
                    }
                    // Higher priority, or the same priority arriving later,
                    // takes the surface. Arrival order rather than RequestedUtc:
                    // requests stamped by the capture worker and by the app come
                    // from different clocks.
                    if (_visible is { } current && current.Event.WorkflowId != notification.WorkflowId && notification.Priority < current.Event.Priority)
                        Enqueue(effects, entry, $"lower-priority-than-visible:{current.Event.Kind}");
                    else Admit(effects, entry);
                }
            }
        }

        // Pixels first. The chime opens a wave device, and doing that on the
        // caller's thread - which is the Avalonia UI thread - used to sit
        // directly in front of the card rasterize.
        effects.Run(_surface, PresentationCompleted, _log);
        if (!string.IsNullOrWhiteSpace(soundVolume))
        {
            var capturedVolume = soundVolume;
            _dispatchSound(() => _playSuccessSound(capturedVolume));
        }
    }

    private void Admit(Effects effects, Entry entry)
    {
        var previous = _visible;
        if (previous is not null && previous.Event.WorkflowId == entry.Event.WorkflowId)
        {
            // One card through Saving and Saved: a later stage that only fell
            // back to the primary display stays where the card already is.
            if (!entry.Event.Target.IsAuthoritative && previous.Event.Target.IsAuthoritative)
                entry.Event = entry.Event with { Target = previous.Event.Target };
        }
        else if (previous is { Presented: false } && IsResult(previous.Event.Kind))
        {
            // A result replaced before it was ever on screen waits its turn.
            previous.Requeued = true;
            Enqueue(effects, previous, $"replaced-before-presentation-by:{entry.Event.Kind}");
        }
        _dismissal?.Dispose();
        _dismissal = null;
        // A requeued result's earlier generation no longer speaks for it.
        if (entry.Generation != 0)
        {
            _inFlight.Remove(entry.Generation);
            if (_timeouts.Remove(entry.Generation, out var stale)) stale.Dispose();
        }
        entry.Generation = ++_generation;
        entry.Requeued = false;
        _visible = entry;
        _inFlight[entry.Generation] = entry;
        _counters.Admit();
        var generation = entry.Generation;
        _timeouts[generation] = _scheduler.Schedule(PresentationTimeout, () => PresentationTimedOut(generation));
        effects.Publish(new ClipOverlayPresentation(generation, entry.Event));
        effects.Trace($"Clip overlay admitted: {Describe(entry.Event)}, generation={generation}, monitor={entry.Event.Target.DeviceName}, target={entry.Event.Target.ReasonLabel}" +
            (previous is null ? "." : $", replaces={previous.Event.Kind}#{previous.Generation}."));
    }

    private void Enqueue(Effects effects, Entry entry, string reason)
    {
        // One waiting card per kind: a newer one of the same kind says the
        // same thing more recently, so a burst of auto-clips queues one card.
        var sameKind = _pending.FindIndex(item => item.Event.Kind == entry.Event.Kind);
        if (sameKind >= 0 && _pending[sameKind].Sequence < entry.Sequence)
        {
            Skip(effects, _pending[sameKind].Event, "coalesced-with-newer-queued");
            _pending.RemoveAt(sameKind);
        }
        else if (sameKind >= 0)
        {
            Skip(effects, entry.Event, "coalesced-with-newer-queued");
            return;
        }
        if (_pending.Count >= PendingCapacity)
        {
            var worst = _pending.Append(entry).OrderBy(item => item.Event.Priority).ThenBy(item => item.Sequence).First();
            Skip(effects, worst.Event, "queue-full");
            if (ReferenceEquals(worst, entry)) return;
            _pending.Remove(worst);
        }
        _pending.Add(entry);
        _counters.Queue();
        effects.Log($"Clip overlay queued: {Describe(entry.Event)}, reason={reason}, waiting={_pending.Count}.");
    }

    // Hands the surface to the most important waiting notification that is
    // still worth showing. False when nothing was admitted.
    private bool Promote(Effects effects)
    {
        var now = _utcNow();
        while (_pending.Count > 0)
        {
            var next = _pending.OrderByDescending(item => item.Event.Priority).ThenBy(item => item.Sequence).First();
            _pending.Remove(next);
            var lifetime = IsResult(next.Event.Kind) ? ResultPendingLifetime : OtherPendingLifetime;
            if (now - next.ArrivedUtc > lifetime || now - next.Event.OccurredUtc > MaximumEventAge)
            {
                Skip(effects, next.Event, "expired-while-queued");
                continue;
            }
            Admit(effects, next);
            return true;
        }
        return false;
    }

    private void PresentationCompleted(ClipOverlayPresentationResult result)
    {
        var effects = new Effects();
        lock (_gate)
        {
            if (_disposed || !_inFlight.Remove(result.Generation, out var entry)) return;
            if (_timeouts.Remove(result.Generation, out var timeout)) timeout.Dispose();
            if (!ReferenceEquals(entry, _visible) || entry.Generation != result.Generation)
            {
                // Replaced while the surface worked on it.
                if (entry.Requeued)
                {
                    // It reached the screen after all; it no longer waits.
                    if (result.Presented && _pending.Remove(entry)) Presented(effects, entry, result, "replaced-after-presentation");
                }
                else if (result.Presented) Presented(effects, entry, result, "replaced-after-presentation");
                else Skip(effects, entry.Event, $"superseded:{result.Reason ?? "replaced"}", result.Generation);
            }
            else if (result.Presented)
            {
                entry.Presented = true;
                Presented(effects, entry, result, null);
                _dismissal?.Dispose();
                var dwell = entry.Event.Kind == ClipOverlayKind.GameStarted ? GameStartedDwell : Dwell;
                _dismissal = _scheduler.Schedule(dwell, () => Dismiss(result.Generation));
            }
            else
            {
                Failed(effects, entry, result.Reason ?? "presentation-failed", result.Generation, timedOut: false);
                _visible = null;
                effects.Dismiss(result.Generation);
                Promote(effects);
            }
        }
        effects.Run(_surface, PresentationCompleted, _log);
    }

    private void PresentationTimedOut(long generation)
    {
        var effects = new Effects();
        lock (_gate)
        {
            if (_disposed || !_inFlight.Remove(generation, out var entry)) return;
            _timeouts.Remove(generation);
            if (ReferenceEquals(entry, _visible) && entry.Generation == generation)
            {
                Failed(effects, entry, "presentation-timeout", generation, timedOut: true);
                _visible = null;
                effects.Dismiss(generation);
                Promote(effects);
            }
            // A requeued result keeps waiting under its next generation.
            else if (!entry.Requeued) Skip(effects, entry.Event, "superseded-without-completion", generation);
        }
        effects.Run(_surface, PresentationCompleted, _log);
    }

    private void Dismiss(long generation)
    {
        var effects = new Effects();
        lock (_gate)
        {
            if (_disposed || _visible is not { } visible || visible.Generation != generation) return;
            _visible = null;
            _dismissal = null;
            // Whatever waited takes the surface directly, without leaving first.
            if (!Promote(effects)) effects.Dismiss(generation);
        }
        effects.Run(_surface, PresentationCompleted, _log);
    }

    public void Dispose()
    {
        var effects = new Effects();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _dismissal?.Dispose();
            foreach (var timeout in _timeouts.Values) timeout.Dispose();
            _timeouts.Clear();
            foreach (var entry in _pending) Skip(effects, entry.Event, "coordinator-disposed");
            _pending.Clear();
            foreach (var entry in _inFlight.Values.Where(entry => !entry.Requeued)) Skip(effects, entry.Event, "coordinator-disposed", entry.Generation);
            _inFlight.Clear();
        }
        effects.Run(null, null, _log);
        _scheduler.Dispose();
        _surface.Dispose();
    }

    private void RememberStage(ClipOverlayEvent notification)
    {
        if (!_workflowStages.ContainsKey(notification.WorkflowId))
        {
            _workflowStageOrder.Enqueue(notification.WorkflowId);
            while (_workflowStageOrder.Count > SoundHistoryLimit)
                _workflowStages.Remove(_workflowStageOrder.Dequeue());
        }
        _workflowStages[notification.WorkflowId] = notification.Stage;
    }

    private static bool IsResult(ClipOverlayKind kind) => kind is ClipOverlayKind.Saved or ClipOverlayKind.Failure;

    private void Presented(Effects effects, Entry entry, ClipOverlayPresentationResult result, string? note)
    {
        var report = result.Report;
        var confirmed = report?.Confirmed ?? true;
        _counters.Present(confirmed);
        var detail = report is null ? string.Empty
            : $", monitor={report.Monitor}, target={entry.Event.Target.ReasonLabel}, backend={report.Backend}, totalMs={report.TotalMs:F1}, " +
              $"queueMs={report.QueueMs:F1}, rasterMs={report.RasterMs:F1}, postMs={report.PostMs:F1}, presentMs={report.PresentMs:F1}, verifyMs={report.VerifyMs:F1}, " +
              $"affinity={report.Affinity}, recovery={report.Recovery}";
        var notes = string.Join(", ", new[] { report?.Note, note }.Where(value => !string.IsNullOrEmpty(value)));
        effects.Log(confirmed
            ? $"Clip overlay presented: {Describe(entry.Event)}, generation={result.Generation}{detail}{(notes.Length > 0 ? $", note={notes}" : string.Empty)}."
            : $"Clip overlay unconfirmed: {Describe(entry.Event)}, generation={result.Generation}, reason={notes}{detail}.");
        effects.Trace($"Clip overlay counters: {_counters.Summary}.");
    }

    private void Failed(Effects effects, Entry entry, string reason, long generation, bool timedOut)
    {
        _counters.Fail(timedOut);
        effects.Log($"Clip overlay failed: {Describe(entry.Event)}, generation={generation}, reason={reason}, monitor={entry.Event.Target.DeviceName}, target={entry.Event.Target.ReasonLabel}.");
        effects.Trace($"Clip overlay counters: {_counters.Summary}.");
    }

    private void Skip(Effects effects, ClipOverlayEvent notification, string reason, long generation = 0)
    {
        _counters.Skip();
        effects.Log($"Clip overlay skipped: {Describe(notification)}{(generation > 0 ? $", generation={generation}" : string.Empty)}, reason={reason}.");
    }

    private static string Describe(ClipOverlayEvent notification)
        => $"id={notification.WorkflowId}, stage={notification.Stage}, kind={notification.Kind}, priority={notification.Priority}";

    private sealed class Entry(ClipOverlayEvent notification, long sequence, DateTime arrivedUtc)
    {
        public ClipOverlayEvent Event { get; set; } = notification;
        public long Sequence { get; } = sequence;
        public DateTime ArrivedUtc { get; } = arrivedUtc;
        public long Generation { get; set; }
        public bool Presented { get; set; }
        // Replaced before it reached the screen and waiting to be shown again.
        public bool Requeued { get; set; }
    }

    // Work decided under the lock and done after it: surface calls must not
    // run under the coordinator's gate, since completions re-enter it.
    private sealed class Effects
    {
        private List<ClipOverlayPresentation>? _publishes;
        private List<long>? _dismissals;
        private List<(bool Debug, string Message)>? _logs;
        public void Publish(ClipOverlayPresentation presentation) => (_publishes ??= new()).Add(presentation);
        public void Dismiss(long generation) => (_dismissals ??= new()).Add(generation);
        public void Log(string message) => (_logs ??= new()).Add((false, message));
        public void Trace(string message) => (_logs ??= new()).Add((true, message));
        public void Run(IClipOverlaySurface? surface, Action<ClipOverlayPresentationResult>? completion, Action<string, bool> log)
        {
            if (_logs is not null)
                foreach (var (debug, message) in _logs) log(message, debug);
            if (surface is null) return;
            // Dismissals first: a promotion after a failure publishes a newer
            // generation, which the surface must not see dismissed.
            if (_dismissals is not null) foreach (var generation in _dismissals) surface.Dismiss(generation);
            if (_publishes is not null && completion is not null) foreach (var presentation in _publishes) surface.Publish(presentation, completion);
        }
    }
}

internal static class ClipOverlayLayout
{
    // Flush horizontal placement makes the overlay read as a screen-edge
    // notification instead of a floating dialog. Keep vertical breathing room.
    private const double VerticalInsetDips = 32;

    public static PixelPoint Position(ClipOverlayTarget target, ClipOverlayPlacement placement, int width, int height)
    {
        var verticalInset = (int)Math.Round(VerticalInsetDips * target.Scaling);
        var area = target.WorkArea;
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var x = left ? area.X : area.Right - width;
        var y = placement switch
        {
            ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.CenterRight => area.Y + (area.Height - height) / 2,
            ClipOverlayPlacement.BottomLeft or ClipOverlayPlacement.BottomRight => area.Bottom - verticalInset - height,
            _ => area.Y + verticalInset
        };
        return new PixelPoint(x, y);
    }

    public static PixelPoint AnimatedPosition(ClipOverlayTarget target, ClipOverlayPlacement placement, int width, int height, double progress)
    {
        var final = Position(target, placement, width, height);
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var travel = (int)Math.Round(Travel(target, width) * (1 - Math.Clamp(progress, 0, 1)));
        var x = final.X + travel * (left ? 1 : -1);
        return new PixelPoint(x, final.Y);
    }

    // The compositor path cannot move the window per frame - DirectComposition
    // animates content inside a window that has to stay where it is for the
    // whole notification. So the window is made `travel` wider than the card
    // and the card slides within it, ending flush against the screen edge.
    // The extra width is taken from the inward side, so the window still fits
    // inside the work area exactly as the moving one did.
    public static ClipOverlayFrameLayout Frame(ClipOverlayTarget target, ClipOverlayPlacement placement, int width, int height)
    {
        var final = Position(target, placement, width, height);
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var travel = (int)Math.Round(Travel(target, width));
        var window = new PixelRect(left ? final.X : final.X - travel, final.Y, width + travel, height);
        return left
            ? new ClipOverlayFrameLayout(window, 0, travel)
            : new ClipOverlayFrameLayout(window, travel, 0);
    }

    // The card slides inward and settles against the edge, and it never leaves
    // the monitor: on a work area too narrow to hold both the card and the
    // travel, the travel is what gives way.
    private static double Travel(ClipOverlayTarget target, int width)
    {
        var area = target.WorkArea;
        var room = Math.Max(0, Math.Max(area.X, area.Right - width) - area.X);
        return Math.Min(24 * target.Scaling, room);
    }
}

// Where the overlay window sits for the life of one notification, plus the two
// horizontal offsets the card animates between inside it: `Rest` is flush
// against the screen edge, `Hidden` is one travel inward.
internal readonly record struct ClipOverlayFrameLayout(PixelRect Window, int RestOffsetX, int HiddenOffsetX);
