namespace ClypDat.App.Services;

// Keeps asynchronous MFT event handling independent from queue timing. A
// TransformNeedInput event grants one input credit; output may always drain,
// including while that credit waits for capture to provide a frame.
internal sealed class MediaFoundationAsyncPump
{
    private int _inputCredits;
    private int _outputCredits;

    public int InputCredits => _inputCredits;
    public bool IsDraining { get; private set; }

    public void OnNeedInput()
    {
        if (!IsDraining) _inputCredits++;
    }

    public bool TryAcceptInput(bool inputAvailable)
    {
        if (!inputAvailable || _inputCredits == 0 || IsDraining) return false;
        _inputCredits--;
        return true;
    }

    // Output never consumes an input credit. MFT output can therefore drain
    // while capture is temporarily unable to satisfy TransformNeedInput.
    public void OnHaveOutput() => _outputCredits++;

    public bool TryDrainOutput()
    {
        if (_outputCredits == 0) return false;
        _outputCredits--;
        return true;
    }

    public bool TryBeginDrain(bool queueCompleted)
    {
        if (!queueCompleted || IsDraining) return false;
        IsDraining = true;
        return true;
    }
}
