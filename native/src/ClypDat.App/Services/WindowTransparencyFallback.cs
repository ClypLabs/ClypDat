using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClypDat.App.Services;

// Avalonia's Windows Server transparency path reports Transparent even when
// DWM paints the window black. Native WS_EX_LAYERED alpha works on the same
// system, so Server overlays use it instead. This is whole-window alpha:
// cards and controls fade slightly with their scrim, but retain their layout
// and remain usable above LibVLC's native child window.
//
// Whole-window alpha is only ever acceptable for a window whose background IS
// the effect - a small badge, or a scrim window carrying nothing else. Hand it
// a full-window scrim that shares a window with dialog content and it fades the
// content too; that is what made the new clips popup render see-through, and why
// dialogs put their scrim in a separate ShareBackdropWindow.
internal static class WindowTransparencyFallback
{
    private sealed class WindowAlpha
    {
        public required byte Value { get; init; }
    }

    private static readonly ConditionalWeakTable<Window, WindowAlpha> OriginalAlphas = new();

    public static void ApplyIfNeeded(Window window, IBrush? background, Action<IBrush> setBackground, string name = "window")
        => Apply(window, background, setBackground, name);

    // Per-pixel mirror windows need their Avalonia owner only for input. Keep
    // that owner effectively invisible without changing its visual tree; its
    // normal render is copied into the native companion before this alpha is
    // applied to the HWND.
    public static void ApplyInputSurfaceIfNeeded(Window window)
    {
        if (!WindowsPlatformProfile.IsServer()) return;

        Win32Properties.SetLayeredWindowOpacity(window, 1d / byte.MaxValue);
    }

    private static void Apply(Window window, IBrush? background, Action<IBrush> setBackground, string name)
    {
        if (background is not ISolidColorBrush solid) return;

        var isServer = WindowsPlatformProfile.IsServer();
        // Set CLYPDAT_TRANSPARENCY_FALLBACK=1 to take this branch on a normal
        // desktop. The condition below is otherwise decided by whether a
        // composition surface existed when the window was created, which is not
        // something a machine can be asked to reproduce on demand - and every
        // report of it so far has been "it looked wrong that one time".
        var forced = Environment.GetEnvironmentVariable("CLYPDAT_TRANSPARENCY_FALLBACK") == "1";
        var mustUseLayeredAlpha = forced || isServer ||
            window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent;
        if (!mustUseLayeredAlpha) return;

        var alpha = OriginalAlphas.GetValue(window, _ => new WindowAlpha { Value = solid.Color.A }).Value;
        var color = solid.Color;
        setBackground(new SolidColorBrush(new Color(255, color.R, color.G, color.B)));

        Win32Properties.SetLayeredWindowOpacity(window, alpha / (double)byte.MaxValue);
        // The only record this ever happened. OverlayTransparencyDiagnostics
        // logs the level BEFORE this runs, so a ghosted window used to leave a
        // log line saying transparency was fine.
        AppLog.Info($"Transparency fallback engaged for {name}: alpha={alpha}; server={isServer}; actual={window.ActualTransparencyLevel}; forced={forced}.");
    }
}
