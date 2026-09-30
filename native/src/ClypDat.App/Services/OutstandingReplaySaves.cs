using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// IDs belong to one history. Recovery resolves them; it never submits them to
// replacement history. Events and replies can both deliver a terminal result.
internal sealed class OutstandingReplaySaves
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ReplaySaveStarted> _active = [];
    private readonly HashSet<Guid> _completed = [];
    internal bool Any { get { lock (_gate) return _active.Count != 0; } }
    internal bool Begin(ReplaySaveStarted save)
    {
        lock (_gate)
        {
            if (_completed.Contains(save.SaveId) || _active.ContainsKey(save.SaveId)) return false;
            _active.Add(save.SaveId, save); return true;
        }
    }
    internal bool Complete(ReplaySaveCompleted save)
    {
        lock (_gate) { _active.Remove(save.SaveId); return _completed.Add(save.SaveId); }
    }
    internal IReadOnlyList<ReplaySaveCompleted> Interrupt(DateTime now)
    {
        lock (_gate)
        {
            var interrupted = _active.Values.Select(save => new ReplaySaveCompleted(save.SaveId, "", null, save.RequestedUtc, now,
                "Replay save interrupted when the capture worker stopped. Completed clips remain saved.", false)).ToArray();
            foreach (var save in interrupted) _completed.Add(save.SaveId);
            _active.Clear(); return interrupted;
        }
    }
}
