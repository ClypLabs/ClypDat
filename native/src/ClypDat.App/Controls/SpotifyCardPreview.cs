using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

internal sealed class SpotifyCardPreview : Control, IDisposable
{
    private SpotifyCardFrames? _frames;
    private (int Width, int Height, string Position, FontFamily Font, bool DynamicBackground, double? OverlayWidth, double? Rotation, bool Right)? _key;
    private (SpotifyCard? Card, double SongSeconds)? _state;
    public bool HasCard => _state?.Card is not null;
    public void Clear()
    {
        _state = null;
        InvalidateVisual();
    }
    public void Update(SpotifyRenderSpec spec, double seconds, int width, int height)
    {
        // Moving a card does not invalidate its cached text, artwork or frame buffer.
        // Translation only changes the owner-window position. Width and
        // rotation change the cached raster itself.
        var key = (width, height, spec.Position, spec.Font, spec.DynamicBackground, spec.Transform?.Width, spec.Transform?.RotationDegrees,
            SpotifyOverlayLayout.IsRight(width, height, spec.Position, spec.Transform));
        if (_key != key)
        {
            _frames?.Dispose();
            _frames = new(width, height, spec.Position, spec.Font, spec.DynamicBackground, spec.Transform);
            _key = key;
            _state = null;
        }
        var state = spec.At(seconds);
        if (_state == state) return;
        _state = state;
        _frames!.Render(state.Card, state.SongSeconds);
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if (_frames is not null) context.DrawImage(_frames.Bitmap, new Rect(_frames.Bitmap.Size), new Rect(Bounds.Size));
    }
    public void Dispose() { _frames?.Dispose(); _frames = null; _key = null; _state = null; }
}
