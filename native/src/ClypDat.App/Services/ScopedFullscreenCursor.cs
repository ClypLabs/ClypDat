using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;

namespace ClypDat.App.Services;

// SetCursor is thread-local. Send WM_SETCURSOR to the window under the pointer
// so the video output's thread also updates its cursor. Subclass each scoped
// HWND, including native children which can override their parent's cursor.
// No ShowCursor counter, class cursor, or system-wide input hook is changed.
internal sealed class ScopedFullscreenCursor : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmSetCursor = 0x0020, WmNcDestroy = 0x0082;
    private static readonly uint DetachMessage = RegisterWindowMessageW("ClypDat.ScopedFullscreenCursor.Detach");
    private readonly Dictionary<nint, CursorHook> _hooks = [];
    private CursorState _state = new(false, default, 0);
    private long _nextRefresh;

    private sealed record CursorState(bool Hidden, PixelPoint Point, nint Owner);

    internal void Update(nint owner, PixelPoint pointer, bool hidden, params nint[] roots)
    {
        var wasHidden = Volatile.Read(ref _state).Hidden;
        var now = Environment.TickCount64;
        var hit = WindowFromPoint(new NativePoint(pointer));
        if (now >= _nextRefresh || !_hooks.ContainsKey(hit))
        {
            _nextRefresh = now + 250;
            foreach (var root in roots)
            {
                if (root == 0) continue;
                Attach(root);
                EnumChildWindows(root, (window, _) => { Attach(window); return true; }, 0);
            }
        }
        Volatile.Write(ref _state, new CursorState(hidden, pointer, owner));
        // Also repairs a direct SetCursor from a child control's cursor update.
        if ((hidden || wasHidden) && IsScopedWindow(hit)) RefreshCursor(hit);
    }

    internal void Reveal()
    {
        Volatile.Write(ref _state, new CursorState(false, default, 0));
        if (GetCursorPos(out var pointer))
        {
            var hit = WindowFromPoint(pointer);
            if (IsScopedWindow(hit)) RefreshCursor(hit);
        }
    }

    internal void Restore()
    {
        Reveal();
        foreach (var hook in _hooks.Values) hook.Detach();
        _hooks.Clear();
        _nextRefresh = 0;
    }

    public void Dispose() => Restore();

    private bool IsScopedWindow(nint window)
    {
        if (!_hooks.TryGetValue(window, out var hook) || hook.Destroyed) return false;
        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    internal static bool ShouldSuppress(bool hidden, PixelPoint idlePointer, PixelPoint pointer,
        nint owner, nint foreground, bool overSurface) =>
        hidden && owner != 0 && foreground == owner && idlePointer == pointer && overSurface;

    private void Attach(nint window)
    {
        if (_hooks.TryGetValue(window, out var previous) && !previous.Destroyed) return;
        GetWindowThreadProcessId(window, out var process);
        if (process != (uint)Environment.ProcessId) return;
        var hook = new CursorHook(this, window);
        if (hook.Attach()) _hooks[window] = hook;
    }

    private bool Suppress(nint window)
    {
        var state = Volatile.Read(ref _state);
        if (!state.Hidden || GetForegroundWindow() != state.Owner || !GetCursorPos(out var pointer)) return false;
        if (pointer.X != state.Point.X || pointer.Y != state.Point.Y) return false;
        var hit = WindowFromPoint(pointer);
        return ShouldSuppress(state.Hidden, state.Point, new PixelPoint(pointer.X, pointer.Y),
            state.Owner, GetForegroundWindow(), hit == window || IsChild(window, hit));
    }

    private static void RefreshCursor(nint window) =>
        SendMessageTimeoutW(window, WmSetCursor, window, 1, 0x0002, 100, out _);

    private sealed class CursorHook
    {
        // A newer native subclass may retain our procedure in its chain. Keep
        // the delegate alive until that HWND dies even after this scope ends.
        private static readonly ConcurrentDictionary<CursorHook, byte> Live = new();
        private readonly ScopedFullscreenCursor _scope;
        private readonly nint _window;
        private readonly WindowProcedure _procedure;
        private readonly nint _procedureAddress;
        private nint _previous;
        private volatile bool _attached;
        private volatile bool _destroyed;
        internal bool Destroyed => _destroyed;

        internal CursorHook(ScopedFullscreenCursor scope, nint window)
        {
            _scope = scope;
            _window = window;
            _procedure = Handle;
            _procedureAddress = Marshal.GetFunctionPointerForDelegate(_procedure);
        }

        internal bool Attach()
        {
            _previous = GetWindowLongPtrW(_window, GwlWndProc);
            if (_previous == 0) return false;
            Live.TryAdd(this, 0);
            _attached = true;
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtrW(_window, GwlWndProc, _procedureAddress);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                _attached = false;
                Live.TryRemove(this, out _);
                return false;
            }
            if (previous != 0) _previous = previous;
            return true;
        }

        internal void Detach()
        {
            _attached = false;
            if (Destroyed) return;
            // Detach on the HWND's own thread. That thread cannot still have
            // an unentered callback using this delegate when its root is freed.
            // A hung thread leaves a passive, rooted procedure until teardown.
            SendMessageTimeoutW(_window, DetachMessage, _procedureAddress, 0, 0x0002, 100, out _);
        }

        private void DetachOnWindowThread()
        {
            if (GetWindowLongPtrW(_window, GwlWndProc) != _procedureAddress) return;
            Marshal.SetLastPInvokeError(0);
            var removed = SetWindowLongPtrW(_window, GwlWndProc, _previous);
            if (removed != 0 || Marshal.GetLastPInvokeError() == 0) Live.TryRemove(this, out _);
        }

        private nint Handle(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == DetachMessage && wParam == _procedureAddress)
            {
                DetachOnWindowThread();
                return 1;
            }
            if (message == WmSetCursor && _attached && _scope.Suppress(window))
            {
                SetCursor(0);
                return 1;
            }
            var result = CallWindowProcW(_previous, window, message, wParam, lParam);
            if (message == WmNcDestroy)
            {
                _attached = false;
                _destroyed = true;
                Live.TryRemove(this, out _);
            }
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X, Y;
        internal NativePoint(PixelPoint point) { X = point.X; Y = point.Y; }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    private delegate bool EnumWindowProcedure(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowProcedure callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
}
