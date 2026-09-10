using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

/// <summary>
/// Watches physical keyboard and mouse activity for as long as it is running,
/// and reports edges. Shared by the clip recorder, which turns edges into a
/// history, and by the settings preview, which lights the keys being held right
/// now.
///
/// Prefers Raw Input over a low-level hook. GlobalHotkeyService documents why
/// that matters: some anti-cheat drivers suppress WH_KEYBOARD_LL delivery while
/// their protected window has focus, and a suppressed hook does not fail - it
/// installs cleanly and simply never fires, so the recording claims an idle
/// keyboard rather than a broken one. Raw Input is a different delivery path and
/// keeps working. The hook stays as a fallback for when registration fails.
///
/// Scan code plus the E0/E1 flags is deliberately what gets reported: a virtual
/// key would lose which side of the keyboard a modifier was on, and would move
/// under the user's keyboard layout, which is exactly what a *physical* overlay
/// must not do.
/// </summary>
internal sealed class PhysicalInputMonitor : IDisposable
{
    public event Action<InputPhysicalKey, bool, string>? Transition;

    /// <summary>False when neither Raw Input nor the hook fallback could be
    /// installed, so an empty history means "not captured", not "idle".</summary>
    public bool Available { get; private set; }
    public bool UsingRawInput { get; private set; }

    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;
    private HookProc? _keyboardProc, _mouseProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _started;
    // A gaming mouse reports movement at up to 1000Hz, and every one of those
    // is a WM_INPUT. The buffer is owned by the message thread and reused, so
    // moving the mouse costs no allocation at all.
    private IntPtr _rawBuffer;
    private int _rawBufferSize;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || !OperatingSystem.IsWindows()) return;
            _started = true;
        }
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "ClypDat physical input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void RunMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        // A message-only window, the same shape GlobalHotkeyService uses: no
        // class to register, and WM_INPUT is delivered to it without the window
        // ever being visible or activatable.
        _hwnd = CreateWindowExW(0, "STATIC", "ClypDat physical input", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            // INPUTSINK: deliver even while another application has focus,
            // which for a game overlay is the only case that matters.
            var devices = new[]
            {
                new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = _hwnd },
                new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevInputSink, Target = _hwnd },
            };
            UsingRawInput = RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RawInputDevice>());
        }
        if (!UsingRawInput) InstallHooks();
        Available = UsingRawInput || _keyboardHook != IntPtr.Zero;
        if (!Available) AppLog.Debug("Physical input capture is unavailable: neither raw input nor a low-level hook could be installed.");
        _ready.Set();

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmInput) HandleRawInput(message.LParam);
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            if (_rawBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_rawBuffer); _rawBuffer = IntPtr.Zero; _rawBufferSize = 0; }
        }
    }

    private void HandleRawInput(IntPtr handle)
    {
        var headerSize = Marshal.SizeOf<RawInputHeader>();
        var size = 0;
        if (GetRawInputData(handle, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size <= 0 || size > 1024) return;
        if (size > _rawBufferSize)
        {
            if (_rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_rawBuffer);
            _rawBuffer = Marshal.AllocHGlobal(size);
            _rawBufferSize = size;
        }
        if (GetRawInputData(handle, RidInput, _rawBuffer, ref size, headerSize) != size) return;
        var header = Marshal.PtrToStructure<RawInputHeader>(_rawBuffer);
        var data = _rawBuffer + headerSize;
        if (header.Type == RimTypeKeyboard)
        {
            var keyboard = Marshal.PtrToStructure<RawKeyboard>(data);
            // A make code of 0 is a device-level event, not a key.
            if (keyboard.MakeCode == 0) return;
            Report(new InputPhysicalKey(keyboard.MakeCode, (keyboard.Flags & RiKeyE0) != 0, (keyboard.Flags & RiKeyE1) != 0),
                (keyboard.Flags & RiKeyBreak) == 0, "Key");
        }
        else if (header.Type == RimTypeMouse)
        {
            var mouse = Marshal.PtrToStructure<RawMouse>(data);
            // The overwhelming majority of mouse reports are pure movement.
            if (mouse.ButtonFlags != 0) ReportMouse(mouse.ButtonFlags);
        }
    }

    private void ReportMouse(ushort flags)
    {
        foreach (var (down, up, button) in MouseButtons)
        {
            if ((flags & down) != 0) Report(new InputPhysicalKey(0, false, false, button), true, "Mouse");
            if ((flags & up) != 0) Report(new InputPhysicalKey(0, false, false, button), false, "Mouse");
        }
    }

    private static readonly (ushort Down, ushort Up, string Button)[] MouseButtons =
    [
        (0x0001, 0x0002, "MouseLeft"), (0x0004, 0x0008, "MouseRight"), (0x0010, 0x0020, "MouseMiddle"),
        // Button 4 is XBUTTON1, the back button; button 5 is XBUTTON2, forward.
        // Both used to report as one "MouseX", so the overlay could not tell
        // them apart and holding both then releasing one read as both released.
        (0x0040, 0x0080, "MouseBack"), (0x0100, 0x0200, "MouseForward"),
    ];

    private void Report(InputPhysicalKey key, bool down, string kind) => Transition?.Invoke(key, down, kind);

    private void InstallHooks()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(data);
            var kind = unchecked((int)message.ToInt64());
            if ((info.Flags & LlkhfInjected) == 0 && kind is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                Report(new InputPhysicalKey((ushort)info.ScanCode, (info.Flags & LlkhfExtended) != 0, (info.Flags & LlkhfExtended1) != 0),
                    kind is WmKeyDown or WmSysKeyDown, "Key");
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var kind = unchecked((int)message.ToInt64());
            var button = kind switch
            {
                WmLButtonDown or WmLButtonUp => "MouseLeft",
                WmRButtonDown or WmRButtonUp => "MouseRight",
                WmMButtonDown or WmMButtonUp => "MouseMiddle",
                // The high word of mouseData says which X button: 1 back, 2 forward.
                WmXButtonDown or WmXButtonUp => (Marshal.PtrToStructure<MsLlHookStruct>(data).MouseData >> 16) == 2
                    ? "MouseForward" : "MouseBack",
                _ => null,
            };
            if (button is not null)
                Report(new InputPhysicalKey(0, false, false, button),
                    kind is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown, "Mouse");
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    public void Dispose()
    {
        var thread = _thread;
        lock (_gate) { _started = false; }
        Transition = null;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        _thread = null; _threadId = 0; _hwnd = IntPtr.Zero;
        _ready.Dispose();
    }

    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205,
        WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    private const uint LlkhfExtended = 0x01, LlkhfInjected = 0x10, LlkhfExtended1 = 0x02;
    private const uint WmInput = 0x00FF, WmQuit = 0x0012;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0, RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 0x01, RiKeyE0 = 0x02, RiKeyE1 = 0x04;
    private static readonly IntPtr HwndMessage = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public uint ExtraInformation; }

    // Explicit: RAWMOUSE puts usButtonFlags inside a union that starts at
    // offset 4, after usFlags and its padding.
    [StructLayout(LayoutKind.Explicit)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct { public MsgPoint Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public MsgPoint Point; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgPoint { public int X, Y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, int count, int size);
    [DllImport("user32.dll")]
    private static extern int GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref int size, int headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
