using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Separate from ShareDialog so its layered alpha never affects the solid card.
internal sealed class ShareBackdropWindow : Window
{
    public event EventHandler? DismissRequested;

    public ShareBackdropWindow(Window owner)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Topmost = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var scrim = new Border { Background = new SolidColorBrush(Color.Parse("#DD000000")) };
        scrim.PointerPressed += Scrim_OnPointerPressed;
        Content = scrim;
        PositionOverOwner(owner);
        Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(this, scrim.Background, b => scrim.Background = b);
    }

    private void Scrim_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint((Visual)sender!).Properties.IsLeftButtonPressed)
            DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PositionOverOwner(Window owner)
    {
        var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            var scaling = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
            Position = new PixelPoint(rect.Left, rect.Top);
            Width = (rect.Right - rect.Left) / scaling;
            Height = (rect.Bottom - rect.Top) / scaling;
            return;
        }

        Position = owner.PointToScreen(new Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
