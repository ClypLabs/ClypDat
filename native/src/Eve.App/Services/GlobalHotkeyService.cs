using System.Runtime.InteropServices;

namespace Eve.App.Services;

// Uses RegisterHotKey instead of a WH_KEYBOARD_LL hook. Some anti-cheat
// drivers (observed with Marvel Rivals' Easy Anti-Cheat) suppress delivery
// of low-level keyboard hooks while their protected game window has focus,
// specifically to block macro/injection tools - which also silently killed
// EVE's save-clip hotkey while actually playing. RegisterHotKey is the
// standard Win32 "global hotkey" API (used by things like Discord's
// push-to-talk) and is delivered via the OS hotkey table rather than a raw
// input hook, so it isn't caught by that kind of filtering.
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const int WmApplyHotkey = 0x8000; // WM_APP
    private static readonly IntPtr HwndMessage = new(-3);

    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _modifiers;
    private uint _vk;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;

    public event EventHandler? Pressed;

    public void SetHotkey(string hotkey)
    {
        var (modifiers, vk) = ToWin32Hotkey(hotkey);
        _modifiers = modifiers;
        _vk = vk;
        // RegisterHotKey/UnregisterHotKey are thread-affine to the window that
        // owns them, so if the hotkey thread is already running, ask it to
        // reapply instead of calling the Win32 APIs from the caller's thread.
        if (_threadId != 0) PostThreadMessage(_threadId, WmApplyHotkey, IntPtr.Zero, IntPtr.Zero);
    }

    public void Start()
    {
        if (_thread is not null || !OperatingSystem.IsWindows()) return;
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "EVE Global Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
        }

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread = null;
        _hwnd = IntPtr.Zero;
        _threadId = 0;
    }

    private void RunMessageLoop()
    {
        _hwnd = CreateWindowExW(0, "STATIC", "EVE Global Hotkey", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _threadId = GetCurrentThreadId();
        if (_hwnd != IntPtr.Zero) ApplyRegistration();
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmHotkey && msg.WParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (msg.Message == WmApplyHotkey)
            {
                ApplyRegistration();
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
    }

    private void ApplyRegistration()
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        if (_vk == 0) return;
        if (!RegisterHotKey(_hwnd, HotkeyId, _modifiers | ModNoRepeat, _vk))
        {
            AppLog.Error($"Global hotkey registration failed for vk=0x{_vk:X}, modifiers=0x{_modifiers:X}.");
        }
    }

    private static (uint Modifiers, uint Vk) ToWin32Hotkey(string hotkey)
    {
        uint modifiers = 0;
        uint vk = 0;
        foreach (var key in HotkeyCombo.Parse(hotkey))
        {
            switch (key)
            {
                case "Ctrl": modifiers |= ModControl; break;
                case "Alt": modifiers |= ModAlt; break;
                case "Shift": modifiers |= ModShift; break;
                case "Win": modifiers |= ModWin; break;
                default:
                    var mapped = ToVirtualKey(key);
                    if (mapped != 0) vk = mapped;
                    break;
            }
        }

        return (modifiers, vk);
    }

    private static uint ToVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') return c;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var fNumber) && fNumber is >= 1 and <= 24)
        {
            return (uint)(0x70 + (fNumber - 1));
        }

        // Only Space/arrows were mapped here - any other key (Home included)
        // silently fell through to 0, and ApplyRegistration treats vk==0 as
        // "no hotkey" and skips RegisterHotKey entirely, no error surfaced
        // anywhere. Filled out with the rest of the keys HotkeyCombo's own
        // capture (MainWindow_OnKeyDown, via Avalonia's Key.ToString()) can
        // actually produce, not just whichever ones happened to get used
        // first.
        return key switch
        {
            "Back" => 0x08,
            "Tab" => 0x09,
            "Return" or "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Space" => 0x20,
            "PageUp" or "Prior" => 0x21,
            "PageDown" or "Next" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            _ => 0
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
