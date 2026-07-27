using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Services;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClypDat.App.Controls;

// LibVLC's picture is a native child HWND, so Avalonia input never reaches a
// visual layered above it. Listen on LibVLC's own output thread instead. This
// has no visual surface, avoiding both transparent-window click-through and
// the grey compositor box seen on some Windows GPU paths.
internal sealed class ClickableVideoView : VideoView
{
    private const int WhMouse = 7;
    private const int HcAction = 0;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;

    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<uint, MouseHook> _mouseHooks = new();
    private MediaPlayer? _mediaPlayer;
    private IntPtr _hostHandle;
    private int _refreshAttempts;
    private bool _disposed;

    public ClickableVideoView()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _refreshTimer.Tick += RefreshTimer_OnTick;
    }

    public event EventHandler? VideoClicked;

    public void WatchMediaPlayer(MediaPlayer? mediaPlayer)
    {
        if (ReferenceEquals(_mediaPlayer, mediaPlayer))
        {
            QueueHookRefresh();
            return;
        }

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Playing -= MediaPlayer_OnPlaying;
            _mediaPlayer.Vout -= MediaPlayer_OnVout;
        }

        _mediaPlayer = mediaPlayer;
        UnhookAll();
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Playing += MediaPlayer_OnPlaying;
            _mediaPlayer.Vout += MediaPlayer_OnVout;
            QueueHookRefresh();
        }
    }

    public void RefreshClickHook() => QueueHookRefresh();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var control = base.CreateNativeControlCore(parent);
        _hostHandle = control.Handle;
        QueueHookRefresh();
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _hostHandle = IntPtr.Zero;
        UnhookAll();
        base.DestroyNativeControlCore(control);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _hostHandle = IntPtr.Zero;
        UnhookAll();
        base.OnDetachedFromVisualTree(e);
    }

    public void DisposeClickHandling()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        WatchMediaPlayer(null);
        UnhookAll();
    }

    private void MediaPlayer_OnPlaying(object? sender, EventArgs e) => QueueHookRefresh();

    private void MediaPlayer_OnVout(object? sender, MediaPlayerVoutEventArgs e) => QueueHookRefresh();

    private void QueueHookRefresh()
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _mediaPlayer is null || _hostHandle == IntPtr.Zero) return;
            _refreshAttempts = 40;
            RefreshHooks();
            if (_mouseHooks.Count == 0) _refreshTimer.Start();
        }, DispatcherPriority.Background);
    }

    private void RefreshTimer_OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _mediaPlayer is null || _hostHandle == IntPtr.Zero)
        {
            _refreshTimer.Stop();
            return;
        }

        RefreshHooks();
        if (_mouseHooks.Count > 0 || --_refreshAttempts <= 0) _refreshTimer.Stop();
    }

    private void RefreshHooks()
    {
        if (!IsWindow(_hostHandle)) return;

        var outputThreads = new HashSet<uint>();
        EnumChildWindows(_hostHandle, (window, _) =>
        {
            // LibVLC suffixes its own HWND handle onto the class name (e.g.
            // "VLC video main 000002131A804D80"), so this has to be a prefix
            // match, not exact equality.
            var className = GetWindowClassName(window);
            if (className.StartsWith("VLC video main", StringComparison.Ordinal) ||
                className.StartsWith("VLC video output", StringComparison.Ordinal))
            {
                uint processId;
                var threadId = GetWindowThreadProcessId(window, out processId);
                if (threadId != 0) outputThreads.Add(threadId);
            }
            return true;
        }, IntPtr.Zero);

        foreach (var threadId in _mouseHooks.Keys.Except(outputThreads).ToArray())
        {
            _mouseHooks[threadId].Dispose();
            _mouseHooks.Remove(threadId);
        }

        foreach (var threadId in outputThreads)
        {
            if (_mouseHooks.ContainsKey(threadId)) continue;
            try
            {
                _mouseHooks.Add(threadId, new MouseHook(threadId, HandleMouseMessage));
                AppLog.Debug($"Editor video click hook attached to VLC thread {threadId}.");
            }
            catch (Exception error)
            {
                AppLog.Error($"Editor video click hook unavailable for VLC thread {threadId}.", error);
            }
        }
    }

    private IntPtr HandleMouseMessage(MouseHook registration, int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HcAction || _disposed || lParam == IntPtr.Zero)
            return CallNextHookEx(registration.Handle, code, wParam, lParam);

        var mouse = Marshal.PtrToStructure<MouseHookStruct>(lParam);
        var message = unchecked((uint)wParam.ToInt64());
        if (message == WmLButtonDown)
        {
            registration.MouseDownInVideo = IsVlcVideoChild(mouse.Window);
        }
        else if (message == WmLButtonUp)
        {
            var wasVideoClick = registration.MouseDownInVideo && IsVlcVideoChild(mouse.Window);
            registration.MouseDownInVideo = false;
            if (wasVideoClick)
            {
                Dispatcher.UIThread.Post(() => VideoClicked?.Invoke(this, EventArgs.Empty));
            }
        }

        return CallNextHookEx(registration.Handle, code, wParam, lParam);
    }

    private bool IsVlcVideoChild(IntPtr window) =>
        _hostHandle != IntPtr.Zero && window != IntPtr.Zero && (window == _hostHandle || IsChild(_hostHandle, window));

    private void UnhookAll()
    {
        _refreshTimer.Stop();
        foreach (var hook in _mouseHooks.Values) hook.Dispose();
        _mouseHooks.Clear();
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var className = new System.Text.StringBuilder(256);
        _ = GetClassNameW(window, className, className.Capacity);
        return className.ToString();
    }

    private sealed class MouseHook : IDisposable
    {
        private readonly HookProc _procedure;
        private IntPtr _handle;

        public MouseHook(uint threadId, Func<MouseHook, int, IntPtr, IntPtr, IntPtr> callback)
        {
            _procedure = (code, wParam, lParam) => callback(this, code, wParam, lParam);
            _handle = SetWindowsHookExW(WhMouse, _procedure, IntPtr.Zero, threadId);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not hook LibVLC mouse input.");
        }

        public bool MouseDownInVideo { get; set; }
        public IntPtr Handle => _handle;

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            if (!UnhookWindowsHookEx(_handle))
                AppLog.Error("Could not release editor video click hook.", new Win32Exception(Marshal.GetLastWin32Error()));
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public int X;
        public int Y;
        public IntPtr Window;
        public uint HitTestCode;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc procedure, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}
