using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClypDat.App.Views;

// Full-owner, opaque drag feedback. It is owned by the card, so it can cover
// both card and dimmer without changing their normal z-order.
internal sealed class ShareDragOverlayWindow : Window
{
    public ShareDragOverlayWindow(Window coveredWindow)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Topmost = false;
        Background = new SolidColorBrush(Color.Parse("#0B1016"));
        Content = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
            Children =
            {
                new Border
                {
                    Width = 72,
                    Height = 56,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = new SolidColorBrush(Color.Parse("#4C6FFF")),
                    BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8),
                },
                new TextBlock
                {
                    Text = "Drag & drop magic, activated!",
                    FontWeight = FontWeight.Bold,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Color.Parse("#EDF4FB")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Drop the clip onto a chat channel, DM, or upload box",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#8EA1B6")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
        PositionOver(coveredWindow);
    }

    private void PositionOver(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
            Position = new PixelPoint(rect.Left, rect.Top);
            Width = (rect.Right - rect.Left) / scaling;
            Height = (rect.Bottom - rect.Top) / scaling;
            return;
        }

        Position = window.PointToScreen(new Point(0, 0));
        Width = window.Bounds.Width;
        Height = window.Bounds.Height;
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
