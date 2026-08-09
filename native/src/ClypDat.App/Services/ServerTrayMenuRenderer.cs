using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ClypDat.App.Services;

// Avalonia owns the native tray popup, but its redirected Server surface makes
// transparent rounded corners opaque. Mirror only that popup through the same
// per-pixel path used by the other Server overlays; Avalonia remains its input
// surface and still owns dismissal, keyboard navigation, and menu commands.
internal sealed class ServerTrayMenuRenderer : IDisposable
{
    private readonly string _trayPopupName;
    private readonly IDisposable _openedSubscription;
    private Window? _pendingPopup;
    private Window? _popup;
    private ServerPerPixelOverlay? _mirror;
    private bool _disposed;

    public ServerTrayMenuRenderer(string toolTipText)
    {
        _trayPopupName = $"AvaloniaTrayPopupRoot_{toolTipText}";
        _openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => QueueAttach(window));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _openedSubscription.Dispose();
        Detach();
    }

    // The class handler runs before the tray popup has finished applying its
    // template and native surface. Wait for its next frame so the mirror gets
    // the rounded template border and the HWND alpha is not overwritten by
    // Avalonia's first redirected render.
    private void QueueAttach(Window popup)
    {
        if (_disposed || !string.Equals(popup.Name, _trayPopupName, StringComparison.Ordinal) || popup.Content is not Control presenter)
            return;

        _pendingPopup = popup;
        popup.RequestAnimationFrame(_ => AttachAfterFirstFrame(popup));
    }

    private void AttachAfterFirstFrame(Window popup)
    {
        if (_disposed || !ReferenceEquals(_pendingPopup, popup) || !popup.IsVisible || popup.Content is not Control presenter)
            return;

        if (!TryGetReadyRoundedCard(popup, presenter, out var roundedCard))
        {
            popup.RequestAnimationFrame(_ => AttachAfterSecondFrame(popup));
            return;
        }

        Attach(popup, presenter, roundedCard);
    }

    private void AttachAfterSecondFrame(Window popup)
    {
        if (_disposed || !ReferenceEquals(_pendingPopup, popup) || !popup.IsVisible || popup.Content is not Control presenter)
            return;

        if (!TryGetReadyRoundedCard(popup, presenter, out var roundedCard))
        {
            AppLog.Error("Server tray popup did not finish layout before mirror attach.");
            _pendingPopup = null;
            return;
        }

        Attach(popup, presenter, roundedCard);
    }

    private static bool TryGetReadyRoundedCard(Window popup, Control presenter, out Border roundedCard)
    {
        roundedCard = presenter.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => string.Equals(border.Name, "LayoutRoot", StringComparison.Ordinal))!;
        return popup.TryGetPlatformHandle()?.Handle != IntPtr.Zero &&
            presenter.Bounds.Width > 0 &&
            presenter.Bounds.Height > 0 &&
            roundedCard is not null &&
            roundedCard.Bounds.Width > 0 &&
            roundedCard.Bounds.Height > 0;
    }

    private void Attach(Window popup, Control presenter, Border roundedCard)
    {
        Detach();
        _pendingPopup = null;
        _popup = popup;
        OverlayTransparencyDiagnostics.Log(popup, "tray-menu");
        var mirror = _mirror = new ServerPerPixelOverlay(popup, roundedCard);
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
            if (!ReferenceEquals(_popup, popup) || !ReferenceEquals(_mirror, mirror)) return;
            mirror.Refresh();
            WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(popup);
        }, DispatcherPriority.Render);
    }

    private void Detach()
    {
        _mirror?.Dispose();
        _mirror = null;
        _popup = null;
        _pendingPopup = null;
    }
}
