using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ClypDat.App.Services;

// Avalonia owns the native tray popup, but its redirected Server surface makes
// transparent rounded corners opaque. Mirror only that popup through the same
// per-pixel path used by the other Server overlays; Avalonia remains its input
// surface and still owns dismissal, keyboard navigation, and menu commands.
internal sealed class ServerTrayMenuRenderer : IDisposable
{
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
        var mirror = _mirror = new ServerPerPixelOverlay(popup, presenter);
        mirror.ShowAndRefresh();
        WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(popup);

        presenter.PointerEntered += (_, _) => QueueRefresh(popup, mirror);
        presenter.PointerMoved += (_, _) => QueueRefresh(popup, mirror);
        presenter.PointerExited += (_, _) => QueueRefresh(popup, mirror);
        popup.KeyDown += (_, _) => QueueRefresh(popup, mirror);
        popup.PositionChanged += (_, _) => QueueRefresh(popup, mirror);
        popup.SizeChanged += (_, _) => QueueRefresh(popup, mirror);
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

    private void Detach()
    {
        _mirror?.Dispose();
        _mirror = null;
        _popup = null;
    }
}
