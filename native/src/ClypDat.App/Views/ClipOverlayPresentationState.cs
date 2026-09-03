namespace ClypDat.App.Views;

internal enum ClipOverlayPresentationPhase
{
    Idle,
    Entering,
    Dwelling,
    Swapping,
    Exiting
}

// Lifecycle order for one reusable native window, kept apart from the window
// itself so it can be tested without a compositor.
//
// Every motion is an awaited animation, and any of them can be superseded
// mid-flight by the next notification. The generation is what lets a
// continuation that resumes after its animation finished tell whether it is
// still the presentation on screen: a stale continuation must not advance the
// phase, restart the dwell, or park a window that now belongs to someone else.
//
// There is deliberately no update QUEUE any more. The window carries two badge
// layers, so a result arriving mid-entry starts a crossing motion from
// wherever the entry currently is instead of waiting for it to land - waiting
// is what made "Clip Saving…" sit there and then snap to "Clip Saved".
internal sealed class ClipOverlayPresentationState
{
    private int _generation;

    public ClipOverlayPresentationPhase Phase { get; private set; } = ClipOverlayPresentationPhase.Idle;

    public bool IsCurrent(int generation) => generation == _generation && Phase != ClipOverlayPresentationPhase.Idle;

    // Starts a new presentation. Bumping the generation here is what abandons
    // every continuation still in flight for the previous one.
    public int Begin()
    {
        _generation++;
        Phase = ClipOverlayPresentationPhase.Entering;
        return _generation;
    }

    public bool EnterCompleted(int generation)
    {
        if (!IsCurrent(generation) || Phase != ClipOverlayPresentationPhase.Entering) return false;
        Phase = ClipOverlayPresentationPhase.Dwelling;
        return true;
    }

    // A new text for the SAME clip. Legal while the badge is still arriving as
    // well as once it has settled; the crossing simply starts from wherever the
    // incoming layer happens to be.
    public bool BeginSwap(int generation)
    {
        if (!IsCurrent(generation)) return false;
        if (Phase is not (ClipOverlayPresentationPhase.Entering or ClipOverlayPresentationPhase.Dwelling)) return false;
        Phase = ClipOverlayPresentationPhase.Swapping;
        return true;
    }

    public bool SwapCompleted(int generation)
    {
        if (!IsCurrent(generation) || Phase != ClipOverlayPresentationPhase.Swapping) return false;
        Phase = ClipOverlayPresentationPhase.Dwelling;
        return true;
    }

    // Exit is reachable from any live phase: the dwell can elapse mid-swap, and
    // a different clip can supersede a badge that is still arriving.
    public bool BeginExit(int generation)
    {
        if (!IsCurrent(generation) || Phase == ClipOverlayPresentationPhase.Exiting) return false;
        Phase = ClipOverlayPresentationPhase.Exiting;
        return true;
    }

    public void Idle()
    {
        _generation++;
        Phase = ClipOverlayPresentationPhase.Idle;
    }
}
