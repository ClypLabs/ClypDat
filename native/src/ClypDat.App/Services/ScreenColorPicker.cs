using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace ClypDat.App.Services;

/// <summary>
/// Samples one pixel anywhere on the desktop: every monitor is captured once,
/// each snapshot is shown frozen in a borderless window over its own monitor,
/// and a loupe follows the pointer.
/// Click picks, Esc cancels, the wheel zooms the loupe.
/// </summary>
/// <remarks>
/// A frozen snapshot rather than reading the live screen under the pointer:
/// the loupe and the window it sits in would otherwise be in the picture, and
/// anything animating would change under the cursor between hover and click.
/// Snapshots stay in memory and are released when the pick ends either way.
/// </remarks>
public static class ScreenColorPicker
{
    // Enough for sixteen 4K displays' worth of pixels, and not an unbounded
    // allocation because a driver reported a silly mode.
    private const long MaxPixels = 64L * 1024 * 1024;
    private const int MaxDisplays = 16;

    private static TaskCompletionSource<Color?>? _pending;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The picked colour, or null when cancelled or capture failed.</summary>
    public static async Task<Color?> PickAsync(Window owner)
    {
        if (_pending is not null || !IsSupported) return null;
        var screens = owner.Screens.All;
        if (screens.Count is 0 or > MaxDisplays || screens.Sum(screen => (long)screen.Bounds.Width * screen.Bounds.Height) > MaxPixels)
        {
            AppLog.Info($"Screen colour pick skipped: {screens.Count} displays exceed the capture budget.");
            return null;
        }

        var snapshots = new List<Snapshot>();
        try
        {
            foreach (var screen in screens) snapshots.Add(Capture(screen.Bounds, screen.Scaling));
        }
        catch (Exception error)
        {
            foreach (var snapshot in snapshots) snapshot.Bitmap.Dispose();
            AppLog.Error("Screen colour capture failed.", error);
            return null;
        }

        var pending = new TaskCompletionSource<Color?>();
        _pending = pending;
        var session = new Session(pending);
        var windows = snapshots.Select(snapshot => new SamplerWindow(snapshot, session)).ToList();
        session.Windows = windows;
        owner.Closed += OwnerClosed;
        foreach (var window in windows) window.Show();
        windows.FirstOrDefault()?.Activate();
        try { return await pending.Task; }
        finally { owner.Closed -= OwnerClosed; }

        void OwnerClosed(object? sender, EventArgs e) => session.Finish(null);
    }

    private sealed class Session(TaskCompletionSource<Color?> pending)
    {
        public List<SamplerWindow> Windows { get; set; } = new();
        // Shared between monitors, so moving across a seam keeps the zoom.
        public int Zoom { get; set; } = 12;

        public void Finish(Color? color)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            _pending = null;
            foreach (var window in Windows) window.Close();
            pending.TrySetResult(color);
        }

        public void Repaint()
        {
            foreach (var window in Windows) window.Sampler.InvalidateVisual();
        }
    }

    private sealed record Snapshot(PixelRect Bounds, double Scaling, WriteableBitmap Bitmap, byte[] Pixels)
    {
        public Color? Pixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Bounds.Width || y >= Bounds.Height) return null;
            var i = (y * Bounds.Width + x) * 4;
            return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
        }
    }

    private sealed class SamplerWindow : Window
    {
        private readonly Session _session;

        public SamplerWindow(Snapshot snapshot, Session session)
        {
            _session = session;
            Sampler = new Sampler(snapshot, session);
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            CanResize = false;
            Topmost = true;
            Title = "ClypDat — Pick screen colour";
            Background = Brushes.Black;
            Position = snapshot.Bounds.Position;
            Width = snapshot.Bounds.Width / snapshot.Scaling;
            Height = snapshot.Bounds.Height / snapshot.Scaling;
            Content = Sampler;
            // The window under the pointer takes the keyboard, so Esc works on
            // whichever monitor the user is looking at.
            PointerEntered += (_, _) => Activate();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _session.Finish(null); } };
            Closed += (_, _) => snapshot.Bitmap.Dispose();
        }

        public Sampler Sampler { get; }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Alt+F4 and the like cancel the whole pick, not just this monitor.
            _session.Finish(null);
            base.OnClosing(e);
        }
    }

    private sealed class Sampler : Control
    {
        private const int Cells = 11;
        private readonly Snapshot _snapshot;
        private readonly Session _session;
        private Point? _pointer;

        public Sampler(Snapshot snapshot, Session session)
        {
            _snapshot = snapshot;
            _session = session;
            Cursor = new Cursor(StandardCursorType.Cross);
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        private (int X, int Y)? PixelAt(Point point)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0 || point.X < 0 || point.Y < 0 || point.X >= Bounds.Width || point.Y >= Bounds.Height) return null;
            var x = Math.Min(_snapshot.Bounds.Width - 1, (int)(point.X / Bounds.Width * _snapshot.Bounds.Width));
            var y = Math.Min(_snapshot.Bounds.Height - 1, (int)(point.Y / Bounds.Height * _snapshot.Bounds.Height));
            return (x, y);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            _pointer = e.GetPosition(this);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _pointer = null;
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            _session.Zoom = Math.Clamp(_session.Zoom + (e.Delta.Y > 0 ? 2 : e.Delta.Y < 0 ? -2 : 0), 4, 24);
            e.Handled = true;
            _session.Repaint();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (PixelAt(e.GetPosition(this)) is { } pixel && _snapshot.Pixel(pixel.X, pixel.Y) is { } color) _session.Finish(color);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.DrawImage(_snapshot.Bitmap, new Rect(0, 0, _snapshot.Bounds.Width, _snapshot.Bounds.Height), bounds);
            if (_pointer is not { } pointer || PixelAt(pointer) is not { } pixel) return;
            DrawLoupe(context, bounds, pointer, pixel);
        }

        // An 11 x 11 pixel loupe, a card below it with the hex value and zoom,
        // and the two hints. The centre pixel is outlined black then white so it
        // reads on any colour.
        private void DrawLoupe(DrawingContext context, Rect screen, Point pointer, (int X, int Y) pixel)
        {
            var zoom = _session.Zoom;
            // Zoom counts physical pixels per sampled pixel.
            var cell = zoom / Math.Max(0.5, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            var family = TextElement.GetFontFamily(this);
            var textBrush = AppThemeService.Brush("TextStrongBrush", "#EDF4FB");
            var hintBrush = AppThemeService.Brush("TextSubtleBrush", "#9FB2C6");
            var hex = _snapshot.Pixel(pixel.X, pixel.Y) is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : string.Empty;
            FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
                new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);
            var label = Text($"{hex}   {zoom}×", 12, textBrush, FontWeight.SemiBold);
            var hint = Text("Click to pick · Esc to cancel", 11, hintBrush);
            var wheel = Text("Scroll to zoom", 11, hintBrush);

            var grid = Cells * cell;
            var width = Math.Max(grid + 12, Math.Max(label.Width, Math.Max(hint.Width, wheel.Width)) + 16);
            var height = grid + 76;
            var x = Math.Max(screen.Left, Math.Min(pointer.X + 24, screen.Right - width));
            var y = Math.Max(screen.Top, Math.Min(pointer.Y + 24, screen.Bottom - height));
            var card = new Rect(x, y, width, height);
            context.DrawRectangle(AppThemeService.Brush("SurfaceRaisedBrush", "#1A242E"), new Pen(AppThemeService.Brush("EdgeStrongBrush", "#2C3B48")), card, 8, 8);

            var origin = new Point(x + (width - grid) / 2, y + 6);
            var empty = AppThemeService.Brush("AppBgBrush", "#0D1116");
            for (var row = 0; row < Cells; row++)
            for (var col = 0; col < Cells; col++)
            {
                var color = _snapshot.Pixel(pixel.X + col - Cells / 2, pixel.Y + row - Cells / 2);
                var brush = color is { } value ? new ImmutableSolidColorBrush(value) : empty;
                context.FillRectangle(brush, new Rect(origin.X + col * cell, origin.Y + row * cell, cell, cell));
            }
            var centre = new Rect(origin.X + Cells / 2 * cell, origin.Y + Cells / 2 * cell, cell, cell);
            context.DrawRectangle(new Pen(Brushes.Black), centre.Inflate(0.5));
            context.DrawRectangle(new Pen(Brushes.White), centre.Deflate(0.5));

            var footer = new Point(x + 8, y + grid + 14);
            context.DrawText(label, footer);
            context.DrawText(hint, new Point(footer.X, footer.Y + 20));
            context.DrawText(wheel, new Point(footer.X, footer.Y + 36));
        }
    }

    // GDI rather than Windows.Graphics.Capture: one synchronous frame of the
    // composed desktop, no capture border, no consent prompt. CAPTUREBLT brings
    // layered windows (tooltips, overlays) into the picture as they are drawn.
    private static Snapshot Capture(PixelRect bounds, double scaling)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed.");
        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            var info = new BitmapInfo
            {
                Size = 40, // BITMAPINFOHEADER alone, not the colour table after it
                Width = bounds.Width,
                Height = -bounds.Height, // top-down rows
                Planes = 1,
                BitCount = 32,
            };
            dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException($"CreateDIBSection failed: error={Marshal.GetLastWin32Error()}.");
            previous = SelectObject(memoryDc, dib);
            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, SrcCopy | CaptureBlt))
                throw new InvalidOperationException($"BitBlt failed: error={Marshal.GetLastWin32Error()}.");
            GdiFlush();

            var pixels = new byte[bounds.Width * bounds.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI leaves the alpha byte undefined; the snapshot is opaque.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var bitmap = new WriteableBitmap(new PixelSize(bounds.Width, bounds.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var locked = bitmap.Lock())
            {
                for (var row = 0; row < bounds.Height; row++)
                    Marshal.Copy(pixels, row * bounds.Width * 4, locked.Address + row * locked.RowBytes, bounds.Width * 4);
            }
            return new Snapshot(bounds, scaling, bitmap, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
