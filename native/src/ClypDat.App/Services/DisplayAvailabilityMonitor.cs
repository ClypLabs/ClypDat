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
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int WtsConsoleDisconnect = 0x1;
    private const int WtsConsoleConnect = 0x2;
    private const int WtsRemoteDisconnect = 0x4;
    private const int WtsRemoteConnect = 0x3;
    private const int NotifyForThisSession = 0;
    private static readonly nint HwndMessage = new(-3);
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static readonly string WindowClassName = $"ClypDat.DisplayAvailability.{Guid.NewGuid():N}";
    private static DisplayAvailabilityMonitor? Current;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CaptureAvailabilityPolicy _policy = new();
    private readonly WndProc _windowProcedure;
    private Thread? _thread;
    private nint _window;
    private nint _powerNotification;
    private bool _lastAvailable = true;

    public DisplayAvailabilityMonitor() => _windowProcedure = WindowProcedure;
    public event EventHandler<bool>? AvailabilityChanged;

    public void Start()
    {
        if (_thread is not null) return;
        Current = this;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "ClypDat Display Availability" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        var window = _window;
        if (window != nint.Zero) PostMessage(window, WmClose, nint.Zero, nint.Zero);
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void MessageLoop()
    {
        var windowClass = new WndClass { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure), lpszClassName = WindowClassName };
        RegisterClass(ref windowClass);
        _window = CreateWindowEx(0, WindowClassName, "ClypDat Display Availability", 0, 0, 0, 0, 0, HwndMessage, nint.Zero, nint.Zero, nint.Zero);
        if (_window != nint.Zero)
        {
            var sessionDisplayStatus = SessionDisplayStatus;
            _powerNotification = RegisterPowerSettingNotification(_window, ref sessionDisplayStatus, 0);
            WTSRegisterSessionNotification(_window, NotifyForThisSession);
        }
        _ready.Set();
        while (GetMessage(out var message, nint.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange && lParam != nint.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == SessionDisplayStatus && setting.DataLength >= sizeof(uint))
                Publish(_policy.SetDisplayState((uint)Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>())));
        }
        else if (message == WmWtsSessionChange)
        {
            var available = wParam.ToInt32() switch
            {
                WtsSessionLock or WtsConsoleDisconnect or WtsRemoteDisconnect => false,
                WtsSessionUnlock or WtsConsoleConnect or WtsRemoteConnect => true,
                _ => _policy.IsAvailable
            };
            Publish(_policy.SetSessionAvailable(available));
        }
        else if (message == WmDestroy)
        {
            if (_powerNotification != nint.Zero) UnregisterPowerSettingNotification(_powerNotification);
            WTSUnRegisterSessionNotification(hwnd);
            PostQuitMessage(0);
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Publish(bool available)
    {
        if (_lastAvailable == available) return;
        _lastAvailable = available;
        AvailabilityChanged?.Invoke(this, available);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; }
    [StructLayout(LayoutKind.Sequential)] private struct WndClass { public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName; public string lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int ptX, ptY; }
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
