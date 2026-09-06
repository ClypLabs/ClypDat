namespace ClypDat.App.Services;

/// <summary>
/// Fires once when a phrase appears in OCR text and re-arms only after it has
/// been gone for a while. Shared by every OCR game detector: HUD banners linger
/// for many frames, so without the latch one banner would fire an event on
/// every sampled frame it is visible for.
///
/// Several phrases can stand for one thing - Overwatch names its highlight both
/// "PLAY OF THE GAME" and "PLAY OF THE MATCH" - and any of them holds the latch,
/// so a screen that swaps one wording for another reads as one continuous event
/// rather than two.
/// </summary>
internal sealed class PhraseLatch
{
    private readonly IReadOnlyList<string> _phrases;
    private readonly int _confirmationFrames;
    private readonly int _resetFrames;
    private int _presentFrames;
    private int _absentFrames;
    private bool _latched;

    public PhraseLatch(string phrase, int confirmationFrames, int resetFrames)
        : this(new[] { phrase }, confirmationFrames, resetFrames)
    {
    }

    public PhraseLatch(IReadOnlyList<string> phrases, int confirmationFrames, int resetFrames)
    {
        _phrases = phrases;
        _confirmationFrames = confirmationFrames;
        _resetFrames = resetFrames;
    }

    public bool Observe(string? text) =>
        ObservePresence(text is not null && _phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The same confirm-and-rearm behaviour for something recognised by
    /// appearance rather than read as text, where the caller has already decided
    /// whether it is on screen.
    /// </summary>
    public bool ObservePresence(bool present)
    {
        if (present)
        {
            _absentFrames = 0;
            _presentFrames++;
            if (!_latched && _presentFrames >= _confirmationFrames)
            {
                _latched = true;
                return true;
            }
        }
        else
        {
            _presentFrames = 0;
            if (_latched && ++_absentFrames >= _resetFrames) Reset();
        }
        return false;
    }

    public void Reset()
    {
        _presentFrames = 0;
        _absentFrames = 0;
        _latched = false;
    }
}
