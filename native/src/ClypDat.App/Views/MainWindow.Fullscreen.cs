using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private readonly FullscreenActivity _fullscreenActivity = new();
    private readonly ScopedFullscreenCursor _fullscreenCursor = new();
    private IPointer? _fullscreenControlsPointer;
    private IPointer? _seekRailPointer;
    private bool _hoverControlsFullscreen;

    private static TimeSpan FullscreenNow => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    private void RecordFullscreenActivity()
    {
        if (ViewModel?.IsVideoFullscreen != true) return;
        _fullscreenActivity.Record(FullscreenNow);
        _fullscreenCursor.Reveal();
    }

    private void SuspendFullscreenPresentation(bool settleSeek = true)
    {
        var pointer = _fullscreenControlsPointer;
        _fullscreenControlsPointer = null;
        FinishSeekRailScrub(settleSeek);
        pointer?.Capture(null);
        _fullscreenActivity.Suspend();
        _fullscreenCursor.Restore();
        HideEditorHoverControls(immediate: true);
    }

    private void PollFullscreenControls()
    {
        var owner = NativeHandleOf(this);
        var enabled = IsVisible && WindowState == WindowState.FullScreen &&
            ViewModel is { IsEditorVisible: true, IsEditorVideoLoading: false } &&
            _playback is not null && !IsEditorSurfaceCovered && GetForegroundWindow() == owner;
        if (!enabled || !GetCursorPos(out var cursor) || FullscreenVideoHost.Bounds.Width <= 0 || FullscreenVideoHost.Bounds.Height <= 0)
        {
            SuspendFullscreenPresentation();
            return;
        }

        var pointer = new PixelPoint(cursor.X, cursor.Y);
        var top = FullscreenVideoHost.PointToScreen(default);
        var bottom = FullscreenVideoHost.PointToScreen(new Point(FullscreenVideoHost.Bounds.Width, FullscreenVideoHost.Bounds.Height));
        var root = GetAncestor(WindowFromPoint(cursor), GaRoot);
        var bar = NativeHandleOf(_editorHoverControlsWindow);
        var scene = NativeHandleOf(_spotifyWindow);
        var overControls = bar != IntPtr.Zero && root == bar && _editorHoverControlsWindow?.IsVisible == true && IsWindowVisible(bar);
        var overSurface = cursor.X >= top.X && cursor.X < bottom.X && cursor.Y >= top.Y && cursor.Y < bottom.Y &&
            (root == owner || (scene != IntPtr.Zero && root == scene) || overControls);
        var captured = _seekRailScrubActive ||
            (_fullscreenControlsPointer?.Captured is { } target && TopLevel.GetTopLevel(target as Visual) == _editorHoverControlsWindow) ||
            _spotifyGesture is not null || _capturedGesture is not null || _timedEffectLayer?.IsGestureActive == true;

        if (_fullscreenActivity.Update(FullscreenNow, pointer, enabled, overSurface, overControls, captured))
        {
            if (_editorHoverControlsWindow is { IsVisible: true } window && !IsWindowVisible(NativeHandleOf(window)))
                HideEditorHoverControls(immediate: true);
            ShowEditorHoverControls();
        }
        else HideEditorHoverControls(immediate: true);

        _fullscreenCursor.Update(owner, pointer, !_fullscreenActivity.ControlsVisible && overSurface,
            owner, NativeHandleOf(_editorHoverControlsWindow), scene);
    }

    private void RepositionFullscreenControls(Window bar, bool force)
    {
        if (FullscreenVideoHost.Bounds.Width <= 0 || FullscreenVideoHost.Bounds.Height <= 0) return;
        var top = FullscreenVideoHost.PointToScreen(default);
        var bottom = FullscreenVideoHost.PointToScreen(new Point(FullscreenVideoHost.Bounds.Width, FullscreenVideoHost.Bounds.Height));
        var viewport = new PixelRect(top, bottom);
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var width = FullscreenControlsGeometry.Width(viewport, scale);
        if (_hoverControlsBackdrop?.Child is not FullscreenPlaybackBar content) return;
        content.SetAvailableWidth(width);
        content.Measure(new Size(width, double.PositiveInfinity));
        var rect = FullscreenControlsGeometry.Place(viewport, scale, content.DesiredSize.Height + 2);
        var handle = NativeHandleOf(bar);
        if (!force && GetWindowRect(handle, out var current) && HoverBarGeometry.MatchesNative(
            current.Left, current.Top, current.Right, current.Bottom, rect.X, rect.Y, rect.Width, rect.Height)) return;

        bar.Width = rect.Width / scale;
        bar.Height = rect.Height / scale;
        bar.Position = rect.Position;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HwndTop, rect.X, rect.Y, rect.Width, rect.Height, SwpNoActivate | SwpNoOwnerZOrder);
        if (bar.IsVisible && _hoverControlsPerPixelOverlay is { IsReady: true } mirror && !mirror.Refresh())
            UseVisibleHoverFallback("fullscreen mirror update failed after reposition");
    }

    private void PlaybackControls_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        RecordFullscreenActivity();
        if (ViewModel?.IsVideoFullscreen == true) _fullscreenControlsPointer = e.Pointer;
    }

    private void PlaybackControls_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _fullscreenControlsPointer = null;
        RecordFullscreenActivity();
    }

    private void PlaybackControls_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_fullscreenControlsPointer?.Captured is { } target && TopLevel.GetTopLevel(target as Visual) == _editorHoverControlsWindow) return;
        _fullscreenControlsPointer = null;
        RecordFullscreenActivity();
    }

    private void FinishSeekRailScrub(bool settle = true)
    {
        var resume = _seekRailScrub.Finish();
        var pointer = _seekRailPointer;
        _seekRailPointer = null;
        pointer?.Capture(null);
        if (resume is { } wasPlaying && settle && ViewModel is { } model)
            _ = ApplyTimelineSeekAsync(model.CurrentTime, wasPlaying);
        if (resume is not null) RecordFullscreenActivity();
    }

    private void FullscreenProgressBar_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => FinishSeekRailScrub();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
