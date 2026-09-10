using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Controls;

/// <summary>Single clipped scene for captured camera and peripherals.</summary>
public sealed class OverlaySceneControl : Canvas
{
    private readonly Image _camera = new() { Stretch = Avalonia.Media.Stretch.Fill, IsHitTestVisible = false };
    private readonly KeyboardOverlayPreview _keyboard = new() { IsHitTestVisible = false };
    public OverlaySceneControl() { ClipToBounds = true; Children.Add(_camera); Children.Add(_keyboard); }
    /// <summary>The bitmap belongs to the playback service, which reuses one
    /// surface for every frame. Disposing it here would destroy the surface the
    /// next frame is about to be painted into, and an unchanged Source
    /// reference does not invalidate on its own.</summary>
    public void SetCamera(Bitmap? image, Rect bounds)
    {
        if (ReferenceEquals(_camera.Source, image)) _camera.InvalidateVisual();
        _camera.Source = image;
        _camera.IsVisible = image is not null;
        SetLeft(_camera, bounds.X); SetTop(_camera, bounds.Y); _camera.Width = bounds.Width; _camera.Height = bounds.Height;
    }
    public void ClearCamera() => SetCamera(null, default);
    public void SetPeripherals(string layout, Rect bounds)
    {
        _keyboard.Layout = layout; _keyboard.IsVisible = true;
        SetLeft(_keyboard, bounds.X); SetTop(_keyboard, bounds.Y); _keyboard.Width = bounds.Width; _keyboard.Height = bounds.Height;
    }
    public void ClearPeripherals() => _keyboard.IsVisible = false;
    public void BeginPointerGesture(PointerPressedEventArgs args) => args.Pointer.Capture(this);
}
