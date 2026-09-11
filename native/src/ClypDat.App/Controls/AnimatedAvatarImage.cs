using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace ClypDat.App.Controls;

/// <summary>
/// A decoded profile picture: one frame for a PNG or JPEG, every frame with its
/// duration for an animated GIF or WebP. Avalonia's Bitmap holds a single
/// frame, so animation is decoded here with SkiaSharp's codec and played by
/// <see cref="AnimatedAvatarImage"/>.
/// </summary>
public sealed class AnimatedAvatar : IDisposable
{
    // Beyond this an avatar is a video; the first 300 frames loop instead.
    private const int MaxFrames = 300;

    private AnimatedAvatar(Bitmap[] frames, TimeSpan[] durations)
    {
        Frames = frames;
        Durations = durations;
    }

    public IReadOnlyList<Bitmap> Frames { get; }
    public IReadOnlyList<TimeSpan> Durations { get; }
    public bool IsAnimated => Frames.Count > 1;

    /// <summary>Decodes <paramref name="bytes"/>, scaling each frame to <paramref name="size"/> pixels wide.</summary>
    public static AnimatedAvatar Decode(byte[] bytes, int size)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unrecognised image format.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameInfo = codec.FrameInfo;
        var count = Math.Clamp(codec.FrameCount, 1, MaxFrames);

        var composed = new SKBitmap?[count];
        var frames = new Bitmap[count];
        var durations = new TimeSpan[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var canvas = new SKBitmap(info);
                composed[i] = canvas;
                // A GIF/WebP frame usually paints only what changed, on top of
                // an earlier frame. The codec names which one; start from it.
                var required = i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;
                if (required >= 0 && required < i && composed[required] is { } prior)
                {
                    using var draw = new SKCanvas(canvas);
                    draw.DrawBitmap(prior, 0, 0);
                }
                var result = codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(i, required));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException($"Frame {i}: {result}.");

                frames[i] = ToAvaloniaBitmap(canvas, size);
                // Browsers treat near-zero delays as 100 ms; so does this, or a
                // badly authored GIF would spin as fast as the timer allows.
                var ms = i < frameInfo.Length ? frameInfo[i].Duration : 0;
                durations[i] = TimeSpan.FromMilliseconds(ms < 20 ? 100 : ms);
            }
            return new AnimatedAvatar(frames, durations);
        }
        catch
        {
            foreach (var frame in frames) frame?.Dispose();
            throw;
        }
        finally
        {
            foreach (var bitmap in composed) bitmap?.Dispose();
        }
    }

    private static Bitmap ToAvaloniaBitmap(SKBitmap frame, int size)
    {
        using var image = SKImage.FromBitmap(frame);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = png.AsStream();
        return frame.Width > size
            ? Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.HighQuality)
            : new Bitmap(stream);
    }

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
}

/// <summary>
/// Draws an <see cref="AnimatedAvatar"/> as a circle, filling its bounds and
/// centred, and plays it while it is on screen. The timer stops when the
/// control leaves the visual tree and skips frames while it is hidden, so a
/// closed Settings page costs nothing.
/// </summary>
public sealed class AnimatedAvatarImage : Control
{
    public static readonly StyledProperty<AnimatedAvatar?> SourceProperty =
        AvaloniaProperty.Register<AnimatedAvatarImage, AnimatedAvatar?>(nameof(Source));

    private DispatcherTimer? _timer;
    private int _frame;

    static AnimatedAvatarImage()
    {
        AffectsRender<AnimatedAvatarImage>(SourceProperty);
    }

    public AnimatedAvatar? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _frame = 0;
            RestartTimer();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RestartTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    private void RestartTimer()
    {
        StopTimer();
        if (Source is not { IsAnimated: true } source || VisualRoot is null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = source.Durations[_frame % source.Durations.Count] };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Source is not { IsAnimated: true } source || _timer is null) { StopTimer(); return; }
        // Hidden (another Settings section, the window minimised): keep the
        // timer at a slow pace instead of drawing frames nobody sees.
        if (!IsEffectivelyVisible)
        {
            _timer.Interval = TimeSpan.FromSeconds(1);
            return;
        }
        _frame = (_frame + 1) % source.Frames.Count;
        _timer.Interval = source.Durations[_frame];
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source || source.Frames.Count == 0) return;
        var bitmap = source.Frames[_frame % source.Frames.Count];
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Fill the circle and centre the picture, cropping the longer side.
        var pixel = bitmap.Size;
        var scale = Math.Max(bounds.Width / pixel.Width, bounds.Height / pixel.Height);
        var cropWidth = bounds.Width / scale;
        var cropHeight = bounds.Height / scale;
        var crop = new Rect((pixel.Width - cropWidth) / 2, (pixel.Height - cropHeight) / 2, cropWidth, cropHeight);

        using (context.PushGeometryClip(new EllipseGeometry(bounds)))
        {
            context.DrawImage(bitmap, crop, bounds);
        }
    }
}
