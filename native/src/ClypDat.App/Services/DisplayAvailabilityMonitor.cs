using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// The capture worker has no Avalonia window, yet it must react when the user
// session's desktop disappears. A message-only Win32 window receives both the
// display-power notification and session lock/disconnect notifications.
internal sealed class DisplayAvailabilityMonitor : IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int PbtPowerSettingChange = 0x8013;
    private const int NotifyForThisSession = 0;
    private static readonly nint HwndMessage = new(-3);
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static readonly string WindowClassName = $"ClypDat.DisplayAvailability.{Guid.NewGuid():N}";
    private static DisplayAvailabilityMonitor? Current;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CaptureAvailabilityPolicy _policy = new();
    private readonly WndProc _windowProcedure;
    private readonly Func<nint, uint, nint> _registerSuspendResume;
    private Thread? _thread;
    private nint _window;
    private nint _powerNotification;
    private nint _suspendNotification;
    private bool _sessionRegistered;
    private volatile bool _disposed;
    private volatile bool _lastAvailable;
    public bool IsAvailable => _lastAvailable;

    public DisplayAvailabilityMonitor() : this(RegisterSuspendResumeNotification) { }

    internal DisplayAvailabilityMonitor(Func<nint, uint, nint> registerSuspendResume)
    {
        _registerSuspendResume = registerSuspendResume;
        _windowProcedure = WindowProcedure;
        _policy.SetMonitoringReady(false);
    }
    public event EventHandler<bool>? AvailabilityChanged;

    public void Start()
    {
        if (_thread is not null) return;
        Current = this;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "ClypDat Display Availability" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            CaptureWorkerLog.Error("Availability monitor initialization timed out; capture remains unavailable.");
    }

    public void Dispose()
    {
        _disposed = true;
        var window = _window;
        if (window != nint.Zero) PostMessage(window, WmClose, nint.Zero, nint.Zero);
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        try
        {
            var windowClass = new WndClass { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure), lpszClassName = WindowClassName };
            if (RegisterClass(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClass failed.");
            _window = CreateWindowEx(0, WindowClassName, "ClypDat Display Availability", 0, 0, 0, 0, 0, HwndMessage, nint.Zero, nint.Zero, nint.Zero);
            if (_window == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
            var sessionDisplayStatus = SessionDisplayStatus;
            _powerNotification = RegisterPowerSettingNotification(_window, ref sessionDisplayStatus, 0);
            if (_powerNotification == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterPowerSettingNotification failed.");
            _suspendNotification = _registerSuspendResume(_window, 0);
            if (_suspendNotification == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterSuspendResumeNotification failed.");
            _sessionRegistered = WTSRegisterSessionNotification(_window, NotifyForThisSession);
            if (!_sessionRegistered) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WTSRegisterSessionNotification failed.");
            Publish(_policy.SetMonitoringReady(!_disposed));
            _ready.Set();
            while (!_disposed)
            {
                var result = GetMessage(out var message, nint.Zero, 0, 0);
                if (result < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
                if (result == 0) break;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error)
        {
            CaptureWorkerLog.Error("Availability monitor initialization/message loop failed; capture unavailable.", error);
        }
        finally
        {
            Publish(_policy.SetMonitoringReady(false));
            _ready.Set();
            if (_powerNotification != nint.Zero) UnregisterPowerSettingNotification(_powerNotification);
            if (_suspendNotification != nint.Zero) UnregisterSuspendResumeNotification(_suspendNotification);
            if (_sessionRegistered) WTSUnRegisterSessionNotification(_window);
            if (_window != nint.Zero) DestroyWindow(_window);
            _window = nint.Zero;
            UnregisterClass(WindowClassName, nint.Zero);
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange && lParam != nint.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == SessionDisplayStatus && setting.DataLength >= sizeof(uint))
            {
                var state = (uint)Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>());
                CaptureWorkerLog.Info($"Display power event: {state}.");
                Publish(_policy.SetDisplayState(state));
            }
        }
        else if (message == WmPowerBroadcast)
        {
            CaptureWorkerLog.Info($"Power event: 0x{wParam.ToInt32():X}.");
            Publish(_policy.HandlePowerEvent(wParam.ToInt32()));
        }
        else if (message == WmWtsSessionChange)
        {
            CaptureWorkerLog.Info($"Session event: 0x{wParam.ToInt32():X}.");
            Publish(_policy.HandleSessionEvent(wParam.ToInt32()));
        }
        else if (message == WmClose)
        {
            PostQuitMessage(0);
            return nint.Zero;
        }
        else if (message == WmDestroy) PostQuitMessage(0);
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Publish(bool available)
    {
        if (_lastAvailable == available) return;
        _lastAvailable = available;
        AvailabilityChanged?.Invoke(this, available);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(nint handle);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [StructLayout(LayoutKind.Sequential)] private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WndClass { public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName; public string lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int ptX, ptY; }
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
