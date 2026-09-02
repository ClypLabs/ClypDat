using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClypDat.App.Controls;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Top-level editor variant: native VLC video cannot be covered by a control in
// MainWindow's visual tree, so the popup gets its own window while the editor
// is open.
//
// It carries the panel and NOTHING else. It used to paint the dim scrim itself,
// in this same window - and when the platform declines real transparency (which
// it does intermittently, most visibly right after a fullscreen game exits)
// WindowTransparencyFallback answers that by applying the scrim's own alpha to
// the WHOLE window. That fades the popup along with its backdrop: 78% opacity
// on the card, the cards, the text and the buttons, with the editor showing
// through all of it. The dim now comes from a separate ShareBackdropWindow, the
// same split ShareDialog already uses ("Separate from ShareDialog so its layered
// alpha never affects the solid card"), so the popup's own background stays
// opaque and no fallback can make it see-through.
internal sealed class NewClipsDialog : Window
{
    private readonly Window _owner;
    public NewClipsPanel Panel { get; } = new();

    public NewClipsDialog(Window owner, EventHandler<RoutedEventArgs> close, EventHandler<RoutedEventArgs> delete, EventHandler<RoutedEventArgs> viewAll)
    {
        _owner = owner;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Width is assigned per show (SetCardWidth); the popup is as tall as its
        // cards make it, up to the panel's own MaxHeight.
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Panel.CloseRequested += close;
        Panel.DeleteRequested += delete;
        Panel.ViewAllRequested += viewAll;
        Content = Panel;

        owner.PositionChanged += Owner_OnPositionChanged;
        owner.SizeChanged += Owner_OnSizeChanged;
        Closed += (_, _) =>
        {
            owner.PositionChanged -= Owner_OnPositionChanged;
            owner.SizeChanged -= Owner_OnSizeChanged;
        };
        // Height only settles after the cards lay out, and the centred position
        // depends on that height.
        Resized += (_, _) => CenterOverOwner();
        Opened += (_, _) =>
        {
            OverlayTransparencyDiagnostics.Log(this, "new-clips-dialog");
            // Opaque background in, so this resolves to full opacity and leaves
            // the popup solid. It stays wired because the rounded corners still
            // want per-pixel alpha where the platform can give it.
            WindowTransparencyFallback.ApplyIfNeeded(this, Panel.PanelBackground, Panel.SetPanelBackground, "new-clips-dialog");
            CenterOverOwner();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) close(this, new RoutedEventArgs()); };
    }

    public void SetCardWidth(double width)
    {
        Width = Math.Clamp(width, 320, Math.Max(320, _owner.Bounds.Width - 32));
        CenterOverOwner();
    }

    public void RefreshOwnerBounds() => CenterOverOwner();

    // Follows the owner rather than being centred once: the popup can be up for
    // as long as the user leaves it there, and the main window can be dragged
    // or resized under it in the meantime.
    private void CenterOverOwner()
    {
        if (_owner.Bounds.Width <= 0 || _owner.Bounds.Height <= 0) return;

        var ownerScaling = _owner.RenderScaling > 0 ? _owner.RenderScaling : 1;
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        var ownerTopLeft = _owner.PointToScreen(new Point(0, 0));
        var ownerWidth = (int)Math.Round(_owner.Bounds.Width * ownerScaling);
        var ownerHeight = (int)Math.Round(_owner.Bounds.Height * ownerScaling);
        var size = FrameSize ?? ClientSize;
        var width = (int)Math.Round(size.Width * scaling);
        var height = (int)Math.Round(size.Height * scaling);

        Position = new PixelPoint(
            ownerTopLeft.X + (ownerWidth - width) / 2,
            ownerTopLeft.Y + (ownerHeight - height) / 2);
    }

    private void Owner_OnPositionChanged(object? sender, PixelPointEventArgs e) => CenterOverOwner();

    private void Owner_OnSizeChanged(object? sender, SizeChangedEventArgs e) => CenterOverOwner();
}
