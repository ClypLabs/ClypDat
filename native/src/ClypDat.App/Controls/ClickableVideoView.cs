using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Services;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using System.Runtime.InteropServices;

namespace ClypDat.App.Controls;

// LibVLC's picture is a native child HWND, so Avalonia input never reaches a
// visual layered above it. A thread-specific WH_MOUSE hook on VLC's own
// output thread (the first approach tried here) installs fine but never
// actually fires - VLC's vout thread doesn't retrieve mouse messages via
// GetMessage/PeekMessage the way WH_MOUSE requires. WH_MOUSE_LL sidesteps
// that entirely: it taps raw input system-wide regardless of what message
// loop (if any) the target thread runs, so we just hit-test the cursor's
// screen position against this control's native window on click.
//
// The hook runs on its OWN thread, and that is not a detail. Windows delivers
// a WH_MOUSE_LL callback by sending a message to the thread that installed the
// hook, so every mouse event on the whole desktop has to round-trip through
// that thread before the system moves the pointer. Installed on the UI thread
// - which is what this did - it put the editor's 60Hz hover-bar poll, Avalonia
// layout and every video-render dispatch directly in the path of raw mouse
// input at whatever rate the mouse polls, and the whole machine's pointer went
// slow and jerky for as long as a clip was open. Windows even gives up on a
// hook that takes longer than LowLevelHooksTimeout (300ms by default) and
// silently starts skipping it. A dedicated thread that does nothing but pump
// messages answers in microseconds no matter how busy the UI is.
internal sealed class ClickableVideoView : VideoView
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmQuit = 0x0012;

    private readonly LowLevelMouseProc _hookProc;
    private readonly object _hookLock = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private int _hookGeneration;
    // Only ever touched on the hook thread.
    private IntPtr _hookHandle;
    private IntPtr _hostHandle;
    private int _nativeSizeSyncQueued;
    private int _nativeWidth;
    private int _nativeHeight;
    // Keep LibVLC's target HWND alive while Library is visible. Avalonia
    // opacity cannot hide a native child, so visibility belongs to Win32.
    private bool _nativeSurfaceVisible;
    private bool _mouseDownInVideo;
    private volatile bool _disposed;

    public ClickableVideoView()
    {
        _hookProc = HandleMouseMessage;
        SizeChanged += (_, _) => RequestNativeSizeSync();
    }

    public event EventHandler? VideoClicked;

    // A null player means the editor is closing this view down. The hook is
    // system-wide, so it has no business outliving the clip that needs it -
    // this used to install it regardless and leave it up for the rest of the
    // session, taxing every mouse event long after the editor was gone.
    public void WatchMediaPlayer(MediaPlayer? mediaPlayer)
    {
        if (mediaPlayer is null) RemoveHook();
        else EnsureHook();
    }

    public void RefreshClickHook() => EnsureHook();

    public void SetNativeSurfaceVisible(bool visible)
    {
        _nativeSurfaceVisible = visible;
        ApplyNativeSurfaceVisibility();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var control = base.CreateNativeControlCore(parent);
        Volatile.Write(ref _hostHandle, control.Handle);
        _nativeWidth = 0;
        _nativeHeight = 0;
        RequestNativeSizeSync();
        ApplyNativeSurfaceVisibility();
        EnsureHook();
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Volatile.Write(ref _hostHandle, IntPtr.Zero);
        _nativeWidth = 0;
        _nativeHeight = 0;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Volatile.Write(ref _hostHandle, IntPtr.Zero);
        _nativeWidth = 0;
        _nativeHeight = 0;
        RemoveHook();
        base.OnDetachedFromVisualTree(e);
    }

    public void DisposeClickHandling()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveHook();
    }

    // Avalonia lays the native host out correctly once the resize settles, but
    // LibVLC's child HWND can lag a live edge drag by a layout pass. Coalesce
    // the stream of layout changes and resize the host to the newest physical
    // pixel bounds on the render queue so the picture follows the window.
    private void RequestNativeSizeSync()
    {
        if (_disposed || Interlocked.Exchange(ref _nativeSizeSyncQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _nativeSizeSyncQueued, 0);
            SyncNativeSize();
        }, DispatcherPriority.Render);
    }

    private void SyncNativeSize()
    {
        var host = Volatile.Read(ref _hostHandle);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = Math.Max(0, (int)Math.Round(Bounds.Width * scaling));
        var height = Math.Max(0, (int)Math.Round(Bounds.Height * scaling));
        if (host == IntPtr.Zero || width == 0 || height == 0 || (width == _nativeWidth && height == _nativeHeight)) return;

        if (SetWindowPos(host, IntPtr.Zero, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate))
        {
            _nativeWidth = width;
            _nativeHeight = height;
        }
    }

    private void ApplyNativeSurfaceVisibility()
    {
        var host = Volatile.Read(ref _hostHandle);
        if (host != IntPtr.Zero) ShowWindow(host, _nativeSurfaceVisible ? SwShow : SwHide);
    }

    private void EnsureHook()
    {
        if (_disposed) return;
        lock (_hookLock)
        {
            if (_disposed || _hookThread is not null) return;
            // Close-then-reopen can start a new hook thread while the previous
            // one is still unwinding. The generation tells a thread whether it
            // is still the current one - without it, a superseded thread would
            // install a hook nobody holds the id for and pump it forever.
            var generation = ++_hookGeneration;
            _hookThread = new Thread(() => HookThreadLoop(generation))
            {
                IsBackground = true,
                Name = "ClypDat-VideoClickHook"
            };
            _hookThread.Start();
        }
    }

    private void RemoveHook()
    {
        Thread? thread;
        uint threadId;
        lock (_hookLock)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
            // Also cancels a thread that hasn't reached its install yet.
            _hookGeneration++;
        }

        if (thread is null) return;
        // WM_QUIT ends the pump below, which unhooks on its way out. Posted,
        // not joined: this runs on the UI thread, and the hook thread can be
        // mid-callback at any moment - waiting on it here is how a deadlock
        // gets built. It's a background thread, so a slow exit costs nothing.
        if (threadId != 0) PostThreadMessageW(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private void HookThreadLoop(int generation)
    {
        var threadId = GetCurrentThreadId();
        lock (_hookLock)
        {
            // Torn down or superseded between Start() and getting here.
            if (_disposed || _hookGeneration != generation) return;
            _hookThreadId = threadId;
        }

        _hookHandle = SetWindowsHookExW(WhMouseLl, _hookProc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            AppLog.Error("Could not install editor video click hook.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        try
        {
            // WH_MOUSE_LL callbacks arrive as messages, so this pump is what
            // actually makes the hook fire - and keeping it otherwise empty is
            // what keeps the answer instant.
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HandleMouseMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        // Every mouse move on the machine lands here, so the cheap rejections
        // come first and nothing is marshalled until a button is actually
        // involved - the pointer's responsiveness is measured by how fast this
        // returns.
        if (code != HcAction || _disposed || lParam == IntPtr.Zero || Volatile.Read(ref _hostHandle) == IntPtr.Zero)
            return CallNextHookEx(_hookHandle, code, wParam, lParam);

        var message = unchecked((uint)wParam.ToInt64());
        if (message == WmLButtonDown)
        {
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            _mouseDownInVideo = IsOverVideo(data.Pt);
        }
        else if (message == WmLButtonUp)
        {
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            var wasVideoClick = _mouseDownInVideo && IsOverVideo(data.Pt);
            _mouseDownInVideo = false;
            if (wasVideoClick)
            {
                Dispatcher.UIThread.Post(() => VideoClicked?.Invoke(this, EventArgs.Empty));
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private bool IsOverVideo(Point point)
    {
        var host = Volatile.Read(ref _hostHandle);
        if (host == IntPtr.Zero) return false;
        var window = WindowFromPoint(point);
        return window != IntPtr.Zero && (window == host || IsChild(host, window));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc procedure, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShow = 5;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
