using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

/// <summary>
/// Global physical input recorder owned by capture worker.  Low-level hooks
/// expose scan code plus E0/E1 flags; virtual-key polling loses both left/right
/// modifier identity and is intentionally not used here.
/// </summary>
internal sealed class RawInputRecorder : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205,
        WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    private const uint LlkhfExtended = 0x01, LlkhfInjected = 0x10, LlkhfExtended1 = 0x02;
    private readonly object _gate = new();
    private readonly List<TimedInputTransition> _transitions = [];
    private readonly List<TimedInputCheckpoint> _checkpoints = [];
    private readonly HashSet<InputPhysicalKey> _down = [];
    private Thread? _thread;
    private IntPtr _keyboardHook, _mouseHook;
    private HookProc? _keyboardProc, _mouseProc;
    private uint _threadId;
    private bool _started, _missing;
    private DateTime _lastCheckpointUtc;
    private const int MaximumTransitions = 100_000;

    public void Start()
    {
        lock (_gate)
        {
            ResetUnderLock();
            if (_started) return;
            _started = true;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "ClypDat raw input" };
            _thread.Start();
        }
    }

    public void Reset()
    {
        lock (_gate) ResetUnderLock();
    }

    /// <param name="mediaScale">Media seconds per wall-clock second. Input is
    /// stamped on the wall clock but replayed against the clip's own timeline,
    /// which runs slower whenever capture dropped frames.</param>
    public InputCaptureIndex Snapshot(DateTime startUtc, DateTime endUtc, double mediaScale = 1)
    {
        lock (_gate)
        {
            // A worker can save before its input thread has attached.  An empty
            // list is not evidence of an idle keyboard in that case.
            if (!_started)
                return new InputCaptureIndex(2, "Keyboard input was not recorded.", [], []);
            CheckpointUnderLock(endUtc);
            var scale = double.IsFinite(mediaScale) && mediaScale > 0 ? mediaScale : 1;
            var transitions = _transitions.Where(x => x.Utc >= startUtc && x.Utc <= endUtc)
                .Select(x => new InputTransition(Math.Max(0, (x.Utc - startUtc).TotalSeconds) * scale, x.Key, x.Down, x.Kind)).ToArray();
            var checkpoints = _checkpoints.Where(x => x.Utc >= startUtc && x.Utc <= endUtc)
                .Select(x => new InputCheckpoint(Math.Max(0, (x.Utc - startUtc).TotalSeconds) * scale, x.Down)).ToArray();
            // A checkpoint at clip start makes reconstruction independent from
            // recorder history which may have aged out before this save.
            var initial = _checkpoints.LastOrDefault(x => x.Utc <= startUtc);
            if (initial is not null) checkpoints = [new InputCheckpoint(0, initial.Down), .. checkpoints];
            return new InputCaptureIndex(2, _missing ? "Input history overflowed or capture reset." : null,
                transitions, checkpoints);
        }
    }

    private void ResetUnderLock()
    {
        _transitions.Clear(); _checkpoints.Clear(); _down.Clear(); _missing = false;
        _lastCheckpointUtc = DateTime.MinValue;
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            lock (_gate) _missing = true;
        try { while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
        finally { if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook); if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook); }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(data);
            var kind = unchecked((int)message.ToInt64());
            if ((info.Flags & LlkhfInjected) == 0 && kind is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                Add(new InputPhysicalKey((ushort)info.ScanCode, (info.Flags & LlkhfExtended) != 0, (info.Flags & LlkhfExtended1) != 0),
                    kind is WmKeyDown or WmSysKeyDown, "Key");
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var kind = unchecked((int)message.ToInt64());
            var button = kind switch { WmLButtonDown or WmLButtonUp => "MouseLeft", WmRButtonDown or WmRButtonUp => "MouseRight", WmMButtonDown or WmMButtonUp => "MouseMiddle", WmXButtonDown or WmXButtonUp => "MouseX", _ => null };
            if (button is not null) Add(new InputPhysicalKey(0, false, false, button), kind is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown, "Mouse");
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void Add(InputPhysicalKey key, bool down, string kind)
    {
        lock (_gate)
        {
            if (!_started || _missing) return;
            var changed = down ? _down.Add(key) : _down.Remove(key);
            if (!changed) return;
            var utc = MonotonicClock.UtcNow;
            if (_transitions.Count >= MaximumTransitions) { _missing = true; _transitions.Clear(); _checkpoints.Clear(); _down.Clear(); return; }
            _transitions.Add(new TimedInputTransition(utc, key, down, kind));
            CheckpointUnderLock(utc);
        }
    }

    private void CheckpointUnderLock(DateTime utc)
    {
        if (_lastCheckpointUtc != DateTime.MinValue && utc - _lastCheckpointUtc < TimeSpan.FromSeconds(2)) return;
        _checkpoints.Add(new TimedInputCheckpoint(utc, _down.ToArray()));
        _lastCheckpointUtc = utc;
    }

    public void Dispose()
    {
        lock (_gate) { _started = false; _down.Clear(); }
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (_thread is { IsAlive: true }) _thread.Join(TimeSpan.FromSeconds(2));
    }

    // v2 intentionally uses clip-relative seconds. UTC values are not stable
    // after a clip is moved, copied, trimmed, or opened on another machine.
    internal sealed record InputCaptureIndex(int Version, string? MissingHistory, IReadOnlyList<InputTransition> Transitions, IReadOnlyList<InputCheckpoint> Checkpoints);
    internal sealed record InputTransition(double Seconds, InputPhysicalKey Key, bool Down, string Kind);
    internal sealed record InputCheckpoint(double Seconds, IReadOnlyList<InputPhysicalKey> Down);
    internal sealed record InputPhysicalKey(ushort ScanCode, bool E0, bool E1, string? MouseButton = null);
    private sealed record TimedInputTransition(DateTime Utc, InputPhysicalKey Key, bool Down, string Kind);
    private sealed record TimedInputCheckpoint(DateTime Utc, IReadOnlyList<InputPhysicalKey> Down);
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public Point Point; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
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
