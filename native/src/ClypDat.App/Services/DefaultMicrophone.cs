using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClypDat.App.Services;

// Windows calls this Role.Multimedia endpoint its general "Default Device"
// (the green checkmark). Role.Communications is a separate user setting.
internal static class DefaultMicrophone
{
    public const Role Role = NAudio.CoreAudioApi.Role.Multimedia;

    public static MMDevice Get(MMDeviceEnumerator enumerator) =>
        enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role);
}

// Core Audio delivers notifications on an arbitrary COM thread. Coalesce
// bursty driver notifications before handing work to UI/capture code.
internal sealed class DefaultMicrophoneWatcher : IMMNotificationClient, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Timer _timer;
    private readonly Action _changed;
    private int _disposed;

    public DefaultMicrophoneWatcher(Action changed)
    {
        _changed = changed;
        _timer = new Timer(_ => NotifyChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    internal static bool IsRelevantDefaultChange(DataFlow flow, Role role) =>
        flow == DataFlow.Capture && (role == Role.Console || role == DefaultMicrophone.Role);

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (IsRelevantDefaultChange(flow, role) && Volatile.Read(ref _disposed) == 0)
        {
            try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void NotifyChanged()
    {
        if (Volatile.Read(ref _disposed) == 0) _changed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _timer.Dispose();
        _enumerator.Dispose();
    }
}
