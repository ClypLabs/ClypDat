namespace ClypDat.App.Services;

internal sealed class CaptureAvailabilityPolicy
{
    private bool _displayAvailable = true;
    private bool _sessionAvailable = true;

    public bool IsAvailable => _displayAvailable && _sessionAvailable;

    public bool SetDisplayState(uint state)
    {
        // PowerMonitorDim remains usable. Only a fully powered-off display must
        // stop the D3D/DXGI capture pipeline.
        _displayAvailable = state != 0;
        return IsAvailable;
    }

    public bool SetSessionAvailable(bool available)
    {
        _sessionAvailable = available;
        return IsAvailable;
    }
}
