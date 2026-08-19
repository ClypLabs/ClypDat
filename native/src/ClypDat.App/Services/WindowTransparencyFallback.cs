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
internal static class WindowTransparencyFallback
{
    private sealed class WindowAlpha
    {
        public required byte Value { get; init; }
    }

    private static readonly ConditionalWeakTable<Window, WindowAlpha> OriginalAlphas = new();

    public static void ApplyIfNeeded(Window window, IBrush? background, Action<IBrush> setBackground)
        => Apply(window, background, setBackground);

    // Per-pixel mirror windows need their Avalonia owner only for input. Keep
    // that owner effectively invisible without changing its visual tree; its
    // normal render is copied into the native companion before this alpha is
    // applied to the HWND.
    public static void ApplyInputSurfaceIfNeeded(Window window)
    {
        if (!WindowsPlatformProfile.IsServer()) return;

        Win32Properties.SetLayeredWindowOpacity(window, 1d / byte.MaxValue);
    }

    private static void Apply(Window window, IBrush? background, Action<IBrush> setBackground)
    {
        if (background is not ISolidColorBrush solid) return;

        var mustUseLayeredAlpha = WindowsPlatformProfile.IsServer() ||
            window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent;
        if (!mustUseLayeredAlpha) return;

        var alpha = OriginalAlphas.GetValue(window, _ => new WindowAlpha { Value = solid.Color.A }).Value;
        var color = solid.Color;
        setBackground(new SolidColorBrush(new Color(255, color.R, color.G, color.B)));

        Win32Properties.SetLayeredWindowOpacity(window, alpha / (double)byte.MaxValue);
    }
}
