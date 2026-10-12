namespace ClypDat.App.Services;

// Replicated pickup fields and pawn identity can arrive in any order. Retain
// actor GUIDs (not reusable channel indices), then count each collected actor
// once after resolving the recorder's PlayerState. Recollecting your own drop
// is inventory management rather than looting.
internal sealed class FortniteLootingTracker
{
    private const int MaximumPawns = 512;
    private const int MaximumPickups = 32768;
    private readonly Dictionary<uint, uint> _pawnStates = [];
    private readonly Dictionary<uint, Pickup> _pickups = [];
    private uint? _recorder;
    private bool _sawCollection;
    internal bool Incomplete { get; private set; }
    internal bool HasUnattributedCollections => _pickups.Values.Any(pickup => pickup.Collected == true &&
        (pickup.Target is not > 0 || !_pawnStates.ContainsKey(pickup.Target.Value)));

    private readonly record struct Pickup(bool? Collected, uint? Target, uint? DroppedBy);

    internal void SetRecorder(uint? recorder) { if (recorder is > 0) _recorder = recorder; }

    internal void RegisterPawn(uint actor, uint playerState)
    {
        if (actor == 0 || playerState == 0) return;
        if (_pawnStates.Count >= MaximumPawns && !_pawnStates.ContainsKey(actor)) { Incomplete = true; return; }
        _pawnStates[actor] = playerState;
    }

    internal void UpdatePickup(uint actor, bool? collected = null, uint? target = null, uint? droppedBy = null)
    {
        if (actor == 0) { Incomplete = true; return; }
        if (_pickups.Count >= MaximumPickups && !_pickups.ContainsKey(actor)) { Incomplete = true; return; }
        if (collected is not null) _sawCollection = true;
        var previous = _pickups.GetValueOrDefault(actor);
        _pickups[actor] = new Pickup(collected ?? previous.Collected, target ?? previous.Target, droppedBy ?? previous.DroppedBy);
    }

    internal void MarkIncomplete() => Incomplete = true;

    // Null means attribution/schema/stream was missing, never an invented zero.
    internal int? Count(bool streamComplete)
    {
        if (!streamComplete || Incomplete || !_sawCollection || _recorder is not { } recorder ||
            !_pawnStates.ContainsValue(recorder)) return null;
        var count = 0;
        foreach (var pickup in _pickups.Values)
        {
            if (pickup.Collected != true) continue;
            // Some remote collections omit the receiving pawn. Keep only
            // positively attributed pickups and expose the count as a lower
            // bound when any collection cannot be attributed.
            if (pickup.Target is not > 0) continue;
            if (!_pawnStates.TryGetValue(pickup.Target.Value, out var state) || state != recorder) continue;
            if (pickup.DroppedBy == pickup.Target || pickup.DroppedBy is { } dropper &&
                _pawnStates.TryGetValue(dropper, out var dropperState) && dropperState == recorder) continue;
            count++;
        }
        return count == 0 && HasUnattributedCollections ? null : count;
    }
}
