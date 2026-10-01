using Avalonia;

namespace ClypDat.App.Views;

// The clock is supplied by the caller so timeout and focus transitions can be
// checked without a dispatcher, native video output, or wall-clock sleeps.
internal sealed class FullscreenActivity
{
    internal static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(1);
    private TimeSpan _lastActivity;
    private PixelPoint? _lastPointer;
    private bool _enabled;

    internal bool ControlsVisible { get; private set; }

    internal void Record(TimeSpan now)
    {
        _lastActivity = now;
        ControlsVisible = true;
    }

    internal void Suspend()
    {
        _enabled = false;
        _lastPointer = null;
        ControlsVisible = false;
    }

    internal bool Update(TimeSpan now, PixelPoint pointer, bool enabled,
        bool overSurface, bool overControls, bool captured)
    {
        if (!enabled)
        {
            Suspend();
            return false;
        }

        if (!_enabled || (overSurface && _lastPointer != pointer) || overControls || captured)
            Record(now);
        _enabled = true;
        _lastPointer = pointer;
        ControlsVisible = now - _lastActivity < HideDelay;
        return ControlsVisible;
    }
}
