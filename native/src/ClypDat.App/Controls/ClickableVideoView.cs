using Avalonia;
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
internal sealed class ClickableVideoView : VideoView
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;

    private readonly LowLevelMouseProc _hookProc;
    private IntPtr _hookHandle;
    private IntPtr _hostHandle;
    private bool _mouseDownInVideo;
    private bool _disposed;

    public ClickableVideoView()
    {
        _hookProc = HandleMouseMessage;
    }

    public event EventHandler? VideoClicked;

    public void WatchMediaPlayer(MediaPlayer? mediaPlayer) => EnsureHook();

    public void RefreshClickHook() => EnsureHook();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var control = base.CreateNativeControlCore(parent);
        _hostHandle = control.Handle;
        EnsureHook();
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _hostHandle = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _hostHandle = IntPtr.Zero;
        RemoveHook();
        base.OnDetachedFromVisualTree(e);
    }

    public void DisposeClickHandling()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveHook();
    }

    private void EnsureHook()
    {
        if (_disposed || _hookHandle != IntPtr.Zero) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _hookHandle != IntPtr.Zero) return;
            _hookHandle = SetWindowsHookExW(WhMouseLl, _hookProc, IntPtr.Zero, 0);
            if (_hookHandle == IntPtr.Zero)
                AppLog.Error("Could not install editor video click hook.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        });
    }

    private void RemoveHook()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HandleMouseMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HcAction || _disposed || _hostHandle == IntPtr.Zero || lParam == IntPtr.Zero)
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
        if (_hostHandle == IntPtr.Zero) return false;
        var window = WindowFromPoint(point);
        return window != IntPtr.Zero && (window == _hostHandle || IsChild(_hostHandle, window));
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
}
