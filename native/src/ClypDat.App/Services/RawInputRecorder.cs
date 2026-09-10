namespace ClypDat.App.Services;

/// <summary>
/// Turns physical input edges into a per-clip history. The platform plumbing
/// lives in PhysicalInputMonitor; this owns only the timeline - edge
/// de-duplication, periodic checkpoints so any seek target is reconstructible,
/// and trimming to a saved clip's window.
/// </summary>
internal sealed class RawInputRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly List<TimedInputTransition> _transitions = [];
    private readonly List<TimedInputCheckpoint> _checkpoints = [];
    private readonly HashSet<InputPhysicalKey> _down = [];
    private readonly PhysicalInputMonitor _monitor = new();
    private bool _started, _missing;
    private DateTime _lastCheckpointUtc;
    private const int MaximumTransitions = 100_000;

    public void Start()
    {
        lock (_gate)
        {
            ResetUnderLock();
            if (_started) return;
            _started = true;
            _monitor.Transition += Add;
        }
        _monitor.Start();
        // Registration failing is a real outcome, and it has to be told apart
        // from a genuinely idle keyboard when the clip is saved.
        if (!_monitor.Available) lock (_gate) _missing = true;
    }

    public void Reset()
    {
        lock (_gate) ResetUnderLock();
    }

    /// <param name="mediaScale">Media seconds per wall-clock second. Input is
    /// stamped on the wall clock but replayed against the clip's own timeline,
    /// which runs slower whenever capture dropped frames.</param>
    public InputCaptureIndex Snapshot(DateTime startUtc, DateTime endUtc, double mediaScale = 1)
    {
        lock (_gate)
        {
            // A worker can save before its input thread has attached.  An empty
            // list is not evidence of an idle keyboard in that case.
            if (!_started)
                return new InputCaptureIndex(2, "Keyboard input was not recorded.", [], []);
            CheckpointUnderLock(endUtc);
            var scale = double.IsFinite(mediaScale) && mediaScale > 0 ? mediaScale : 1;
            var transitions = _transitions.Where(x => x.Utc >= startUtc && x.Utc <= endUtc)
                .Select(x => new InputTransition(Math.Max(0, (x.Utc - startUtc).TotalSeconds) * scale, x.Key, x.Down, x.Kind)).ToArray();
            var checkpoints = _checkpoints.Where(x => x.Utc >= startUtc && x.Utc <= endUtc)
                .Select(x => new InputCheckpoint(Math.Max(0, (x.Utc - startUtc).TotalSeconds) * scale, x.Down)).ToArray();
            // A checkpoint at clip start makes reconstruction independent from
            // recorder history which may have aged out before this save.
            var initial = _checkpoints.LastOrDefault(x => x.Utc <= startUtc);
            if (initial is not null) checkpoints = [new InputCheckpoint(0, initial.Down), .. checkpoints];
            return new InputCaptureIndex(2, _missing ? "Input history overflowed or capture reset." : null,
                transitions, checkpoints);
        }
    }

    private void ResetUnderLock()
    {
        _transitions.Clear(); _checkpoints.Clear(); _down.Clear(); _missing = false;
        _lastCheckpointUtc = DateTime.MinValue;
    }

    private void Add(InputPhysicalKey key, bool down, string kind)
    {
        lock (_gate)
        {
            if (!_started || _missing) return;
            var changed = down ? _down.Add(key) : _down.Remove(key);
            if (!changed) return;
            var utc = MonotonicClock.UtcNow;
            if (_transitions.Count >= MaximumTransitions) { _missing = true; _transitions.Clear(); _checkpoints.Clear(); _down.Clear(); return; }
            _transitions.Add(new TimedInputTransition(utc, key, down, kind));
            CheckpointUnderLock(utc);
        }
    }

    private void CheckpointUnderLock(DateTime utc)
    {
        if (_lastCheckpointUtc != DateTime.MinValue && utc - _lastCheckpointUtc < TimeSpan.FromSeconds(2)) return;
        _checkpoints.Add(new TimedInputCheckpoint(utc, _down.ToArray()));
        _lastCheckpointUtc = utc;
    }

    public void Dispose()
    {
        lock (_gate) { _started = false; _down.Clear(); }
        _monitor.Transition -= Add;
        _monitor.Dispose();
    }

    // The recorded shape lives in ClipInputIndex.cs, shared with the reader.
    private sealed record TimedInputTransition(DateTime Utc, InputPhysicalKey Key, bool Down, string Kind);
    private sealed record TimedInputCheckpoint(DateTime Utc, IReadOnlyList<InputPhysicalKey> Down);
}
