using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Save start and completion share one target. Other notifications never borrow
// that session, so an old result cannot turn back into a new save in place.
internal sealed class ClipOverlaySessionManager
{
    private readonly Func<ClipOverlayTarget> _resolveTarget;
    private readonly TimeSpan _maximumSaveAge;
    private ClipOverlaySession? _activeSave;

    public ClipOverlaySessionManager(Func<ClipOverlayTarget> resolveTarget, TimeSpan? maximumSaveAge = null)
    {
        _resolveTarget = resolveTarget;
        _maximumSaveAge = maximumSaveAge ?? TimeSpan.FromSeconds(30);
    }

    public ClipOverlaySession BeginSave(string trigger)
    {
        var session = Create(trigger);
        _activeSave = session;
        return session;
    }

    public ClipOverlaySession CompleteSave(string trigger)
    {
        if (_activeSave is { } active && active.Age < _maximumSaveAge)
        {
            _activeSave = null;
            return active;
        }

        _activeSave = null;
        return Create(trigger);
    }

    public ClipOverlaySession CreateStandalone(string trigger) => Create(trigger);

    public void OnHidden(ClipOverlaySession session)
    {
        if (ReferenceEquals(_activeSave, session)) _activeSave = null;
    }

    private ClipOverlaySession Create(string trigger) => new(trigger, _resolveTarget());
}
