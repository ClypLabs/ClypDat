using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;

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
    private readonly IDefaultMicrophoneNotificationRegistration _registration;
    private readonly Timer _timer;
    private readonly Action _changed;
    private GCHandle _callbackRoot;
    private int _registered;
    private int _disposed;

    public DefaultMicrophoneWatcher(Action changed) : this(changed, new CoreAudioNotificationRegistration()) { }

    internal DefaultMicrophoneWatcher(Action changed, IDefaultMicrophoneNotificationRegistration registration)
    {
        _changed = changed;
        _registration = registration;
        _timer = new Timer(_ => NotifyChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Core Audio owns only a native callback pointer. Keep an explicit managed
        // root until a successful unregister proves Windows no longer can call it.
        _callbackRoot = GCHandle.Alloc(this);
        try
        {
            _registration.Register(this);
            Volatile.Write(ref _registered, 1);
        }
        catch
        {
            _callbackRoot.Free();
            _timer.Dispose();
            _registration.Dispose();
            throw;
        }
    }

    internal static bool IsRelevantDefaultChange(DataFlow flow, Role role) =>
        flow == DataFlow.Capture && (role == Role.Console || role == DefaultMicrophone.Role);

    internal bool CallbackRooted => _callbackRoot.IsAllocated;

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
        _timer.Dispose();
        if (Volatile.Read(ref _registered) == 0) return;
        try
        {
            _registration.Unregister(this);
            Volatile.Write(ref _registered, 0);
            _callbackRoot.Free();
            _registration.Dispose();
        }
        catch (Exception error)
        {
            // Keep COM enumerator and root alive. Callback is inert after dispose;
            // freeing it while MMDevAPI still owns it is a native use-after-free.
            AppLog.Error("Default microphone watcher unregistration failed; retaining inert callback.", error);
        }
    }
}

internal interface IDefaultMicrophoneNotificationRegistration : IDisposable
{
    void Register(IMMNotificationClient callback);
    void Unregister(IMMNotificationClient callback);
}

internal sealed class CoreAudioNotificationRegistration : IDefaultMicrophoneNotificationRegistration
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public void Register(IMMNotificationClient callback) => _enumerator.RegisterEndpointNotificationCallback(callback);
    public void Unregister(IMMNotificationClient callback) => _enumerator.UnregisterEndpointNotificationCallback(callback);
    public void Dispose() => _enumerator.Dispose();
}
