using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

// Each panel owns its decoded pixels. Changing clips clears the old outfit
// before fetching the new one; a detached panel cannot publish a late result.
public sealed class FortniteOutfitPortrait : Control
{
    public static readonly StyledProperty<string?> OutfitIdProperty =
        AvaloniaProperty.Register<FortniteOutfitPortrait, string?>(nameof(OutfitId));
    private Bitmap? _bitmap;
    private CancellationTokenSource? _request;
    private int _generation;
    private bool _attached;

    public FortniteOutfitPortrait() => IsVisible = false;
    public string? OutfitId { get => GetValue(OutfitIdProperty); set => SetValue(OutfitIdProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OutfitIdProperty) Restart();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void Restart()
    {
        Clear();
        if (!_attached || FortniteOutfitPortraitCache.NormaliseId(OutfitId) is not { } id) return;
        var generation = _generation;
        _request = new CancellationTokenSource();
        _ = LoadAsync(id, generation, _request.Token);
    }

    private void Clear()
    {
        ++_generation;
        var request = _request;
        _request = null;
        request?.Cancel();
        request?.Dispose();
        // The compositor may still be drawing the last frame that used it.
        DeferredBitmapDisposal.Release(_bitmap);
        _bitmap = null;
        IsVisible = false;
        InvalidateVisual();
    }

    private async Task LoadAsync(string id, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await FortniteOutfitPortraitCache.Default.GetBytesAsync(id, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_attached || cancellationToken.IsCancellationRequested || generation != _generation ||
                    FortniteOutfitPortraitCache.NormaliseId(OutfitId) != id) return;
                // Header dimensions and encoded size were checked before any
                // decode. Construct/assign/dispose Avalonia bitmaps on the UI.
                using var stream = new MemoryStream(bytes, writable: false);
                var decodeWidth = Math.Min(88, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
                var bitmap = Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
                if (bitmap.PixelSize.Width is <= 0 or > 512 || bitmap.PixelSize.Height is <= 0 or > 512) { bitmap.Dispose(); return; }
                _bitmap = bitmap;
                IsVisible = true;
                InvalidateVisual();
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Artwork is optional; offline/unsupported cosmetics remain hidden.
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is not { } bitmap) return;
        var bounds = new Rect(Bounds.Size);
        var pixels = bitmap.Size;
        var scale = Math.Max(bounds.Width / pixels.Width, bounds.Height / pixels.Height);
        if (!double.IsFinite(scale) || scale <= 0) return;
        var source = new Rect((pixels.Width - bounds.Width / scale) / 2, (pixels.Height - bounds.Height / scale) / 2,
            bounds.Width / scale, bounds.Height / scale);
        using (context.PushGeometryClip(new RectangleGeometry(bounds, 6, 6))) context.DrawImage(bitmap, source, bounds);
    }
}
