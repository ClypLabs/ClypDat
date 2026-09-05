namespace ClypDat.App.Services;

/// <summary>
/// Fires once when a phrase appears in OCR text and re-arms only after it has
/// been gone for a while. Shared by every OCR game detector: HUD banners linger
/// for many frames, so without the latch one banner would fire an event on
/// every sampled frame it is visible for.
/// </summary>
internal sealed class PhraseLatch(string phrase, int confirmationFrames, int resetFrames)
{
    private int _presentFrames;
    private int _absentFrames;
    private bool _latched;

    public bool Observe(string? text)
    {
        var present = text?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;
        if (present)
        {
            _absentFrames = 0;
            _presentFrames++;
            if (!_latched && _presentFrames >= confirmationFrames)
            {
                _latched = true;
                return true;
            }
        }
        else
        {
            _presentFrames = 0;
            if (_latched && ++_absentFrames >= resetFrames) Reset();
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
