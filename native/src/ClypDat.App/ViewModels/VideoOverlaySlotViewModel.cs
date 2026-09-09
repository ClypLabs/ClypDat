using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Layout;

namespace ClypDat.App.ViewModels;

/// <summary>
/// One window in the overlay preview. A corner used to be drawn twice - once as
/// a fixed picker card and again as a free-floating layer - so the same camera
/// appeared in two places at two sizes. The window is now the layer: it sits
/// where the overlay will burn in, at the size the overlay will burn in, and
/// carries its own source dropdown.
/// </summary>
public sealed class VideoOverlaySlotViewModel(VideoOverlayViewModel overlay, string corner) : ViewModelBase
{
    // The dropdown hangs outside the window so the window's own rectangle stays
    // an honest preview of the burned-in area.
    private const double ChromeHeight = 42;

    public VideoOverlayViewModel Overlay { get; } = overlay;
    public string Corner { get; } = corner;
    public string Label { get; } = corner.ToUpperInvariant();
    public ObservableCollection<OverlaySourceOption> Sources => Overlay.Sources;
    public string KeyboardLayout => Overlay.KeyboardLayout;

    public OverlaySourceOption? Source { get => Overlay.SourceAt(Corner); set => Overlay.SetSource(Corner, value); }
    public OverlaySourceKind Kind => Source?.Kind ?? OverlaySourceKind.None;
    public bool IsCamera => Kind == OverlaySourceKind.Camera;
    public bool IsKeyboard => Kind == OverlaySourceKind.Keyboard;
    public bool IsEmpty => !IsCamera && !IsKeyboard;

    /// <summary>The layer this window drags, or null while the corner is empty.</summary>
    public string? Layer => IsCamera ? "Camera" : IsKeyboard ? "Keyboard" : null;
    public bool IsMovable => Layer is not null;
    public bool IsSelected => Layer is { } layer && Overlay.IsLayerSelected(layer);

    public double Left => Overlay.SlotRect(Corner).X;
    public double Top => Overlay.SlotRect(Corner).Y;
    public double Width => Overlay.SlotRect(Corner).Width;
    public double Height => Overlay.SlotRect(Corner).Height;

    // A window near the bottom of the canvas would push its dropdown off the
    // preview, so the dropdown flips above it.
    private bool ChromeAbove => Top + Height + ChromeHeight > Overlay.PreviewHeight;
    public VerticalAlignment ChromeAlignment => ChromeAbove ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    public Thickness ChromeMargin => ChromeAbove ? new Thickness(0, -ChromeHeight, 0, 0) : new Thickness(0, 0, 0, -ChromeHeight);

    public void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Source), nameof(Kind), nameof(IsCamera), nameof(IsKeyboard), nameof(IsEmpty),
                     nameof(Layer), nameof(IsMovable), nameof(IsSelected), nameof(KeyboardLayout),
                     nameof(Left), nameof(Top), nameof(Width), nameof(Height),
                     nameof(ChromeAlignment), nameof(ChromeMargin)
                 })
            OnPropertyChanged(name);
    }
}
