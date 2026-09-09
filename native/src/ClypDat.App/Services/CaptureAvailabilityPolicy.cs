namespace ClypDat.App.Services;

internal sealed class CaptureAvailabilityPolicy
{
    private bool _displayAvailable = true;
    private bool _sessionAvailable = true;
    private bool _sessionConnected = true;
    private bool _systemAwake = true;
    private bool _monitorReady = true;

    public bool IsAvailable => _monitorReady && _systemAwake && _displayAvailable && _sessionAvailable && _sessionConnected;

    public bool SetMonitoringReady(bool ready) { _monitorReady = ready; return IsAvailable; }
    public bool SetDisplayState(uint state) { _displayAvailable = state != 0; return IsAvailable; }
    public bool SetSessionAvailable(bool available) { _sessionAvailable = available; return IsAvailable; }

    public bool HandlePowerEvent(int value)
    {
        switch (value)
        {
            case 0x4: _systemAwake = false; break; // PBT_APMSUSPEND
            case 0x7: // PBT_APMRESUMESUSPEND
            case 0x12: _systemAwake = true; break; // PBT_APMRESUMEAUTOMATIC
        }
        return IsAvailable;
    }

    public bool HandleSessionEvent(int value)
    {
        switch (value)
        {
            case 0x1: // WTS_CONSOLE_CONNECT
            case 0x3: _sessionConnected = true; break; // WTS_REMOTE_CONNECT
            case 0x2: // WTS_CONSOLE_DISCONNECT
            case 0x4: _sessionConnected = false; break; // WTS_REMOTE_DISCONNECT
            case 0x7: _sessionAvailable = false; break; // WTS_SESSION_LOCK
            case 0x8: _sessionAvailable = true; break; // WTS_SESSION_UNLOCK
        }
        return IsAvailable;
    }
}
