#if CLYPDAT_LINUX
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using ClypDat.App.Services;
using LibVLCSharp.Shared;

namespace ClypDat.App.Controls;

internal sealed class ClickableVideoView : Control
{
    private readonly DispatcherTimer _timer;
    public MediaPlayer? MediaPlayer { get; set; }
    public event EventHandler? VideoClicked;
    public ClickableVideoView()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) VideoClicked?.Invoke(this, EventArgs.Empty); };
    }
    public void WatchMediaPlayer(MediaPlayer? player) { MediaPlayer = player; if (player is null) _timer.Stop(); else _timer.Start(); }
    public void RefreshClickHook() { }
    public void DisposeClickHandling() => _timer.Stop();
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var output = LinuxEditorVideoOutput.For(MediaPlayer);
        if (output?.Acquire() is not { } frame) return;
        var scale = Math.Min(Bounds.Width / frame.Width, Bounds.Height / frame.Height);
        var width = frame.Width * scale; var height = frame.Height * scale;
        output.Release(frame);
        // Retained scene operations must not lease all three decoder buffers.
        // Acquire the latest picture only when the compositor actually paints.
        context.Custom(new FrameDraw(output, new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height)));
    }
    private sealed class FrameDraw(LinuxEditorVideoOutput output, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            if (output.Acquire() is not { } frame) return;
            try { output.Draw(lease.SkCanvas, frame, bounds); }
            finally { output.Release(frame); }
        }
        public void Dispose() { }
    }
}
#endif
