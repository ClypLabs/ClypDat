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
    public void SetCamera(Bitmap? image, Rect bounds)
    {
        if (!ReferenceEquals(_camera.Source, image)) (_camera.Source as IDisposable)?.Dispose();
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
