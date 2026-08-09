using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// Avalonia owns the native tray popup, but its redirected Server surface makes
// transparent rounded corners opaque. Mirror only that popup through the same
// per-pixel path used by the other Server overlays; Avalonia remains its input
// surface and still owns dismissal, keyboard navigation, and menu commands.
internal sealed class ServerTrayMenuRenderer : IDisposable
{
    private const double CornerRadius = 8;
    private readonly string _trayPopupName;
    private readonly IDisposable _openedSubscription;
    private Window? _popup;
    private ServerPerPixelOverlay? _mirror;
    private bool _disposed;

    public ServerTrayMenuRenderer(string toolTipText)
    {
        _trayPopupName = $"AvaloniaTrayPopupRoot_{toolTipText}";
        _openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _openedSubscription.Dispose();
        Detach();
    }

    private void Attach(Window popup)
    {
        if (_disposed || !string.Equals(popup.Name, _trayPopupName, StringComparison.Ordinal) || popup.Content is not Control presenter)
            return;

        Detach();
        _popup = popup;
        ApplyRoundedShape(popup, presenter);
        var mirror = _mirror = new ServerPerPixelOverlay(popup, presenter);
        mirror.ShowAndRefresh();
        WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(popup);

        presenter.PointerEntered += (_, _) => QueueRefresh(popup, mirror);
        presenter.PointerMoved += (_, _) => QueueRefresh(popup, mirror);
        presenter.PointerExited += (_, _) => QueueRefresh(popup, mirror);
        popup.KeyDown += (_, _) => QueueRefresh(popup, mirror);
        popup.PositionChanged += (_, _) => QueueRefresh(popup, mirror);
        popup.SizeChanged += (_, _) =>
        {
            ApplyRoundedShape(popup, presenter);
            QueueRefresh(popup, mirror);
        };
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_popup, popup)) return;
            Detach();
        };
    }

    private void QueueRefresh(Window popup, ServerPerPixelOverlay mirror)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_popup, popup) && ReferenceEquals(_mirror, mirror)) mirror.Refresh();
        }, DispatcherPriority.Render);
    }

    // Border CornerRadius only shapes its own paint. The mirrored render and
    // the Server popup HWND need an explicit rounded clip or transparent
    // corners become the popup's square redirected surface.
    private static void ApplyRoundedShape(Window popup, Control presenter)
    {
        var width = presenter.Bounds.Width;
        var height = presenter.Bounds.Height;
        if (width > 0 && height > 0)
            presenter.Clip = new RectangleGeometry(new Rect(0, 0, width, height), CornerRadius, CornerRadius);

        var handle = popup.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;

        var pixelWidth = bounds.Right - bounds.Left;
        var pixelHeight = bounds.Bottom - bounds.Top;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        var scaling = popup.RenderScaling > 0 ? popup.RenderScaling : 1;
        var diameter = Math.Max(1, (int)Math.Round(CornerRadius * scaling * 2));
        var region = CreateRoundRectRgn(0, 0, pixelWidth + 1, pixelHeight + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;

        // Windows owns region after a successful SetWindowRgn call.
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }

    private void Detach()
    {
        _mirror?.Dispose();
        _mirror = null;
        _popup = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectNative rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
