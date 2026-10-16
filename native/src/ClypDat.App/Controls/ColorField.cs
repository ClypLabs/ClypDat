using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClypDat.App.Controls;

// The theme editor's colour field and hue bar: the colour under the pointer
// is a function of where the pointer is and nothing else. Avalonia's
// ColorSpectrum instead looked the colour up in a table it rebuilt on a
// background thread, and drifted from the cursor.
//
// The field is a plain HSV square, as Discord's picker is: saturation across
// from white on the left to the full hue on the right, value up from black at
// the bottom. Every colour is on offer at full brightness and every pixel is
// exactly the colour picking it gives. Turning a pick into something a theme
// can paint - a ground dark or light enough for text, an accent that stands
// out on it - is the theme's job, and the editor says when it did so; it is
// not done by hiding colours from the field.

/// <summary>
/// What a colour field shows at one hue: HSV saturation across, value up.
/// </summary>
public sealed class ColorFieldModel
{
    public static ColorFieldModel Hsv { get; } = new();

    private ColorFieldModel() { }

    /// <summary>The colour at a point, x and y as fractions of the field, clamped to it.</summary>
    public Color At(double hue, double x, double y)
        => new HsvColor(1, Math.Clamp(hue, 0, 359.99), Math.Clamp(x, 0, 1), 1 - Math.Clamp(y, 0, 1)).ToRgb();

    /// <summary>Where a colour sits in the field.</summary>
    public Point Locate(Color color)
    {
        var hsv = color.ToHsv();
        return new Point(Math.Clamp(hsv.S, 0, 1), 1 - Math.Clamp(hsv.V, 0, 1));
    }

    /// <summary>
    /// Fills a width x height BGRA buffer with the field, sampling each pixel
    /// at its centre with <see cref="At"/>, so the pixel under the thumb is the
    /// colour the thumb picks.
    /// </summary>
    public void Fill(double hue, int width, int height, int[] pixels)
    {
        // Rows are independent, so they are spread over the cores: a hue drag
        // redraws the whole field on every move.
        Parallel.For(0, height, row =>
        {
            var y = (row + 0.5) / height;
            for (var column = 0; column < width; column++)
            {
                var color = At(hue, (column + 0.5) / width, y);
                pixels[row * width + column] = unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
            }
        });
    }
}

/// <summary>A <see cref="ColorFieldModel"/> at <see cref="Hue"/>, with the thumb at <see cref="Position"/>.</summary>
public sealed class ColorField : Control
{
    public static readonly StyledProperty<ColorFieldModel?> ModelProperty =
        AvaloniaProperty.Register<ColorField, ColorFieldModel?>(nameof(Model));
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<ColorField, double>(nameof(Hue));
    // Fractions of the field, 0-1 each way. The view model turns it into a
    // colour with the model, so the field and the colour cannot disagree.
    public static readonly StyledProperty<Point> PositionProperty =
        AvaloniaProperty.Register<ColorField, Point>(nameof(Position), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ColorField, double>(nameof(CornerRadius), 8);

    private WriteableBitmap? _bitmap;
    private (ColorFieldModel? Model, double Hue, PixelSize Size) _drawn;

    static ColorField()
    {
        AffectsRender<ColorField>(ModelProperty, HueProperty, PositionProperty, CornerRadiusProperty);
    }

    public ColorField() => Cursor = new Cursor(StandardCursorType.Cross);

    public ColorFieldModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public Point Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The position of a point, as fractions of the field, clamped to it.</summary>
    public static Point At(Point point, Size size) => new(
        size.Width > 0 ? Math.Clamp(point.X / size.Width, 0, 1) : 0,
        size.Height > 0 ? Math.Clamp(point.Y / size.Height, 0, 1) : 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Captured, so a drag that leaves the field keeps steering the colour
        // along its edge instead of stopping where the pointer crossed it.
        e.Pointer.Capture(this);
        e.Handled = true;
        SetCurrentValue(PositionProperty, At(e.GetPosition(this), Bounds.Size));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Handled = true;
        SetCurrentValue(PositionProperty, At(e.GetPosition(this), Bounds.Size));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var model = Model;
        if (rect.Width <= 0 || rect.Height <= 0 || model is null) return;

        // One bitmap pixel per screen pixel, so nothing is interpolated between
        // what was computed and what is shown.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = new PixelSize(Math.Max(1, (int)Math.Round(rect.Width * scaling)), Math.Max(1, (int)Math.Round(rect.Height * scaling)));
        if (_bitmap is null || _drawn != (model, Hue, size))
        {
            var pixels = new int[size.Width * size.Height];
            model.Fill(Hue, size.Width, size.Height, pixels);
            if (_bitmap is null || _bitmap.PixelSize != size)
            {
                _bitmap?.Dispose();
                // 96 DPI, so its Size is its pixel size. DrawImage(image, rect)
                // passes Size as the source rect and Skia reads that rect in
                // pixels: at the screen's DPI a 348px field at 150% said 232,
                // and only its top-left two thirds were drawn, stretched.
                _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (var locked = _bitmap.Lock())
            {
                for (var row = 0; row < size.Height; row++)
                    Marshal.Copy(pixels, row * size.Width, locked.Address + row * locked.RowBytes, size.Width);
            }
            _drawn = (model, Hue, size);
        }

        using (context.PushClip(new RoundedRect(rect, CornerRadius)))
            context.DrawImage(_bitmap, rect);

        var position = Position;
        DrawThumb(context, new Point(position.X * rect.Width, position.Y * rect.Height), null);
    }

    internal static void DrawThumb(DrawingContext context, Point centre, IBrush? fill)
    {
        // White ring inside a dark one, so it reads on every colour the field
        // can show without switching between light and dark selectors.
        context.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 4), centre, 7, 7);
        context.DrawEllipse(null, new Pen(Brushes.White, 2), centre, 7, 7);
    }
}

/// <summary>Hue from 0° at the left to 360° at the right, as a rounded bar under the field.</summary>
public sealed class HueStrip : Control
{
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<HueStrip, double>(nameof(Hue), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private static readonly LinearGradientBrush Hues = CreateHues();

    private static LinearGradientBrush CreateHues()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        for (var i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(new HsvColor(1, i * 60 % 360, 1, 1).ToRgb(), i / 6.0));
        return brush;
    }

    static HueStrip()
    {
        AffectsRender<HueStrip>(HueProperty);
    }

    public HueStrip() => Cursor = new Cursor(StandardCursorType.Hand);

    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }

    /// <summary>
    /// The hue at a distance along the bar, clamped to it. The thumb's centre
    /// travels between the bar's rounded ends, so the ends are reachable
    /// without the thumb hanging off them. Never 360, which wraps to red.
    /// </summary>
    public static double At(double x, double width, double inset)
    {
        var travel = width - 2 * inset;
        return travel > 0 ? Math.Min(359.99, Math.Clamp((x - inset) / travel, 0, 1) * 360) : 0;
    }

    private double Inset => Bounds.Height / 2;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        SetCurrentValue(HueProperty, At(e.GetPosition(this).X, Bounds.Width, Inset));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Handled = true;
        SetCurrentValue(HueProperty, At(e.GetPosition(this).X, Bounds.Width, Inset));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = rect.Height / 2;
        context.DrawRectangle(Hues, null, rect, radius, radius);
        var hue = Hue;
        var centre = new Point(radius + hue / 360 * Math.Max(0, rect.Width - 2 * radius), rect.Height / 2);
        ColorField.DrawThumb(context, centre, new SolidColorBrush(new HsvColor(1, hue, 1, 1).ToRgb()));
    }
}
