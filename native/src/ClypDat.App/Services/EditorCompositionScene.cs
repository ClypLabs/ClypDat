using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ClypDat.App.Controls;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Copies artwork, never video, into the native presentation scene.</summary>
internal sealed class EditorCompositionScene
{
    private NativeVideoOutput? _output;
    private ulong _nextId = 4;
    private long _cameraRevision = -1, _spotifyRevision = -1;
    private string? _keyboardKey;
    // The overlay window may be hidden while a dialog covers the editor or
    // while the video is parked for startup. Keep already-uploaded artwork in
    // the native scene during that interval: dropping it would replace a
    // complete scene with an empty one before the next presentation.
    private Rect? _cameraBounds, _keyboardBounds, _spotifyBounds;
    private readonly Dictionary<Guid, (ulong Id, TimedVideoEffect Effect, int Width, int Height, int FrameHeight)> _texts = [];

    internal void Update(NativeVideoOutput output, MainWindowViewModel model, TimeSpan time, long anchorMicroseconds,
        OverlaySceneControl? captured = null, SpotifyCardPreview? spotify = null, double displayWidth = 0, double displayHeight = 0)
    {
        if (_output != output)
        {
            _output = output;
            _texts.Clear(); _cameraRevision = _spotifyRevision = -1; _keyboardKey = null;
            _cameraBounds = _keyboardBounds = _spotifyBounds = null;
        }
        var sourceWidth = model.SelectedSourceWidth; var sourceHeight = model.SelectedSourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return;
        var crop = model.ActiveCropRect ?? new ClipRenderFilters.CropRect(0, 0, sourceWidth, sourceHeight);
        NativeVideoOutput.Rectangle SourceBounds(Rect normalized) => new(new Rect(
            (crop.X + normalized.X * crop.Width) / sourceWidth, (crop.Y + normalized.Y * crop.Height) / sourceHeight,
            normalized.Width * crop.Width / sourceWidth, normalized.Height * crop.Height / sourceHeight));
        var blurs = model.BlurEffects.Where(e => e.Visible).Select(e => new NativeVideoOutput.Blur
        {
            Bounds = SourceBounds(new Rect(e.X, e.Y, e.Width, e.Height)),
            Start = e.Start,
            End = e.End,
            Sigma = (float)(e.Strength * crop.Height / 1080),
            Shape = e.Shape switch { "Rounded" => 1u, "Ellipse" => 2u, _ => 0u }
        }).ToArray();
        List<NativeVideoOutput.Artwork> artwork = [];
        void Add(ulong id, Rect normalized, uint layer, double start = 0, double end = double.MaxValue) => artwork.Add(new()
        {
            Id = id,
            Bounds = SourceBounds(normalized),
            Layer = layer,
            Start = start,
            End = end
        });
        if (displayWidth > 0 && displayHeight > 0 && captured is not null)
        {
            Rect Normalize(Rect r) => new(r.X / displayWidth, r.Y / displayHeight, r.Width / displayWidth, r.Height / displayHeight);
            if (captured.CameraArtwork is { } camera)
            {
                if (_cameraRevision != captured.CameraRevision) { output.UpdateArtwork(1, camera); _cameraRevision = captured.CameraRevision; }
                _cameraBounds = Normalize(captured.CameraArtworkBounds);
                Add(1, _cameraBounds.Value, 0);
            }
            if (captured.KeyboardArtwork is { } keyboard)
            {
                var bounds = Normalize(captured.KeyboardArtworkBounds);
                var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * crop.Width));
                var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * crop.Height));
                var key = $"{keyboard.Layout}|{width}|{height}|{keyboard.CustomBoard?.GetHashCode()}|{string.Join(',', (keyboard.PressedKeys ?? new HashSet<string>()).Order())}";
                if (_keyboardKey != key)
                {
                    var clone = new KeyboardOverlayPreview { Layout = keyboard.Layout, PressedKeys = keyboard.PressedKeys, CustomBoard = keyboard.CustomBoard, Width = width, Height = height };
                    clone.Measure(new Size(width, height)); clone.Arrange(new Rect(0, 0, width, height));
                    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                    bitmap.Render(clone); output.UpdateArtwork(2, bitmap); _keyboardKey = key;
                }
                _keyboardBounds = bounds;
                Add(2, bounds, 0);
            }
            if (spotify?.Artwork is { } card)
            {
                if (_spotifyRevision != spotify.ArtworkRevision) { output.UpdateArtwork(3, card); _spotifyRevision = spotify.ArtworkRevision; }
                _spotifyBounds = Normalize(new Rect(Canvas.GetLeft(spotify), Canvas.GetTop(spotify), spotify.Width, spotify.Height));
                Add(3, _spotifyBounds.Value, 0);
            }
        }
        // No display-space geometry exists while the window is parked. Reuse
        // last geometry only for layers that remain enabled in the model.
        if (captured?.CameraArtwork is null && model.HasCameraOverlayLayer && model.CameraOverlayLayerVisible && _cameraBounds is { } cameraBounds)
            Add(1, cameraBounds, 0);
        if (captured?.KeyboardArtwork is null && model.HasPeripheralOverlayLayer && model.PeripheralOverlayLayerVisible && _keyboardBounds is { } keyboardBounds)
            Add(2, keyboardBounds, 0);
        if (spotify?.Artwork is null && model.Settings.SpotifyOverlayEnabled && model.SpotifyOverlayLayerVisible && _spotifyBounds is { } spotifyBounds)
            Add(3, spotifyBounds, 0);
        foreach (var effect in model.TextEffects.Where(e => e.Visible))
        {
            var width = Math.Max(1, (int)Math.Ceiling(effect.Width * crop.Width));
            var height = Math.Max(1, (int)Math.Ceiling(effect.Height * crop.Height));
            if (!_texts.TryGetValue(effect.Id, out var cached) || cached.Effect != effect || cached.Width != width || cached.Height != height || cached.FrameHeight != crop.Height)
            {
                var id = cached.Id == 0 ? _nextId++ : cached.Id;
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                using (var context = bitmap.CreateDrawingContext()) TimedEffectPainter.DrawText(context, effect, new Rect(0, 0, width, height), crop.Height);
                output.UpdateArtwork(id, bitmap); cached = (id, effect, width, height, crop.Height); _texts[effect.Id] = cached;
            }
            Add(cached.Id, new Rect(effect.X, effect.Y, effect.Width, effect.Height), 1, effect.Start, effect.End);
        }
        foreach (var id in _texts.Keys.Where(id => !model.TextEffects.Any(e => e.Visible && e.Id == id)).ToArray()) _texts.Remove(id);
        if (!model.HasCameraOverlayLayer || !model.CameraOverlayLayerVisible) { _cameraRevision = -1; _cameraBounds = null; }
        if (!model.HasPeripheralOverlayLayer || !model.PeripheralOverlayLayerVisible) { _keyboardKey = null; _keyboardBounds = null; }
        if (!model.Settings.SpotifyOverlayEnabled || !model.SpotifyOverlayLayerVisible) { _spotifyRevision = -1; _spotifyBounds = null; }
        output.Submit(blurs, artwork.ToArray(), time, model.IsPlaying ? model.ClipSpeed : 0, anchorMicroseconds);
        output.ReadStatus();
    }
}
