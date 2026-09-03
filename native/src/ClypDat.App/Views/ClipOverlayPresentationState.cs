namespace ClypDat.App.Views;

internal enum ClipOverlayPresentationPhase
{
    Hidden,
    Preparing,
    Entering,
    Dwelling,
    Exiting
}

internal enum ClipOverlayUpdateDisposition
{
    Ignore,
    Queue,
    Apply,
    Restart
}

// Keeps frame callbacks and result updates from racing one reusable native
// window. The window owns rendering; this type owns only lifecycle order.
internal sealed class ClipOverlayPresentationState
{
    private int _generation;
    private ClipOverlayRequest? _queuedUpdate;

    public ClipOverlayPresentationPhase Phase { get; private set; } = ClipOverlayPresentationPhase.Hidden;

    public int Begin()
    {
        _generation++;
        _queuedUpdate = null;
        Phase = ClipOverlayPresentationPhase.Preparing;
        return _generation;
    }

    public bool IsCurrent(int generation) => generation == _generation && Phase != ClipOverlayPresentationPhase.Hidden;

    public bool BeginEntry(int generation)
    {
        if (!IsCurrent(generation) || Phase != ClipOverlayPresentationPhase.Preparing) return false;
        Phase = ClipOverlayPresentationPhase.Entering;
        return true;
    }

    public ClipOverlayUpdateDisposition Update(int generation, ClipOverlayRequest request)
    {
        if (!IsCurrent(generation)) return ClipOverlayUpdateDisposition.Ignore;
        if (Phase is ClipOverlayPresentationPhase.Preparing or ClipOverlayPresentationPhase.Entering)
        {
            _queuedUpdate = request;
            return ClipOverlayUpdateDisposition.Queue;
        }

        return Phase switch
        {
            ClipOverlayPresentationPhase.Dwelling => ClipOverlayUpdateDisposition.Apply,
            ClipOverlayPresentationPhase.Exiting => ClipOverlayUpdateDisposition.Restart,
            _ => ClipOverlayUpdateDisposition.Ignore
        };
    }

    public ClipOverlayRequest? CompleteEntry(int generation)
    {
        if (!IsCurrent(generation) || Phase != ClipOverlayPresentationPhase.Entering) return null;
        Phase = ClipOverlayPresentationPhase.Dwelling;
        var queued = _queuedUpdate;
        _queuedUpdate = null;
        return queued;
    }

    public bool BeginExit()
    {
        if (Phase != ClipOverlayPresentationPhase.Dwelling) return false;
        Phase = ClipOverlayPresentationPhase.Exiting;
        return true;
    }

    public void Hide()
    {
        _generation++;
        _queuedUpdate = null;
        Phase = ClipOverlayPresentationPhase.Hidden;
    }
}
