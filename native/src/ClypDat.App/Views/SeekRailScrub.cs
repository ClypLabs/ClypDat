namespace ClypDat.App.Views;

internal sealed class SeekRailScrub
{
    internal bool Active { get; private set; }
    private bool _wasPlaying;

    internal bool Begin(bool playing)
    {
        if (Active) return false;
        Active = true;
        _wasPlaying = playing;
        return true;
    }

    // Release and capture loss can both arrive for one drag. Only the first
    // completion carries a resume intent and may start a settling seek.
    internal bool? Finish()
    {
        if (!Active) return null;
        Active = false;
        return _wasPlaying;
    }
}
