using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClypDat.App.Controls;

// The theme editor's colour field and hue strip: the colour under the
// pointer is a function of where the pointer is and nothing else.
// Avalonia's ColorSpectrum instead looked the colour up in a table it
// rebuilt on a background thread, and drifted from the cursor.
//
// The field is also only ever colours the theme paints exactly. A plain HSV
// square showed colours the theme then clamped into its band - a top edge of
// #4D3A52 painted as #2E1E33 - so the swatch never matched the point picked.
// Here every pixel is computed by the same function a pick is, with the
// theme's own painting applied, so what is under the thumb is what is painted.

/// <summary>
/// What a colour field shows at one hue: HSL saturation across, perceived
/// lightness (CIE L*) up, each cropped to a range. Lightness rather than HSV
/// value, because the theme's bands are bands of luminance: at one value a
/// green is seven times brighter than a blue.
/// </summary>
public sealed class ColorFieldModel
{
    // HSL lightness is searched on this grid, so a pick and the pixel drawn
    // for it land on the same step.
    private const int Steps = 1024;
    private static readonly double[] LinearChannel = CreateLinearChannel();
    private readonly Func<Color, Color>? _paint;

    /// <param name="minSaturation">HSL saturation at the left edge, 0-1.</param>
    /// <param name="maxSaturation">HSL saturation at the right edge, 0-1.</param>
    /// <param name="minLightness">CIE L* at the bottom edge, 0-100.</param>
    /// <param name="maxLightness">CIE L* at the top edge, 0-100.</param>
    /// <param name="paint">How the theme paints a pick; applied to every colour the field gives.</param>
    public ColorFieldModel(double minSaturation, double maxSaturation, double minLightness, double maxLightness, Func<Color, Color>? paint = null)
    {
        MinSaturation = minSaturation;
        MaxSaturation = maxSaturation;
        MinLightness = minLightness;
        MaxLightness = maxLightness;
        _paint = paint;
    }

    public double MinSaturation { get; }
    public double MaxSaturation { get; }
    public double MinLightness { get; }
    public double MaxLightness { get; }

    /// <summary>The colour at a point, x and y as fractions of the field, clamped to it.</summary>
    public Color At(double hue, double x, double y)
    {
        var saturation = SaturationAt(x);
        var target = LuminanceAt(y);
        int low = 0, high = Steps - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Luminance(Step(hue, saturation, middle)) >= target) high = middle;
            else low = middle + 1;
        }
        return Paint(Step(hue, saturation, low));
    }

    /// <summary>Where a colour sits in the field, clamped to it.</summary>
    public Point Locate(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255d;
        var min = Math.Min(color.R, Math.Min(color.G, color.B)) / 255d;
        var hslLightness = (max + min) / 2;
        var saturation = max - min < 1e-9 ? 0 : hslLightness > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min);
        return new Point(
            Fraction(saturation, MinSaturation, MaxSaturation),
            1 - Fraction(Lightness(Luminance(color)), MinLightness, MaxLightness));
    }

    /// <summary>
    /// Fills a width x height BGRA buffer with the field, sampling each pixel
    /// at its centre. Exactly <see cref="At"/> for every pixel: the same
    /// search, with each column's steps cached as the rows visit them.
    /// </summary>
    public void Fill(double hue, int width, int height, int[] pixels)
    {
        var targets = new double[height];
        for (var row = 0; row < height; row++) targets[row] = LuminanceAt((row + 0.5) / height);
        // Columns are independent, so they are spread over the cores: a hue
        // drag redraws the whole field on every move.
        Parallel.For(0, width,
            () => (Luminances: new double[Steps], Painted: new Dictionary<int, int>()),
            (column, _, local) =>
            {
                var saturation = SaturationAt((column + 0.5) / width);
                Array.Fill(local.Luminances, double.NaN);
                for (var row = 0; row < height; row++)
                {
                    int low = 0, high = Steps - 1;
                    while (low < high)
                    {
                        var middle = (low + high) / 2;
                        if (double.IsNaN(local.Luminances[middle])) local.Luminances[middle] = Luminance(Step(hue, saturation, middle));
                        if (local.Luminances[middle] >= targets[row]) high = middle;
                        else low = middle + 1;
                    }
                    var raw = Step(hue, saturation, low);
                    var key = (raw.R << 16) | (raw.G << 8) | raw.B;
                    if (!local.Painted.TryGetValue(key, out var pixel))
                    {
                        var color = Paint(raw);
                        pixel = unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
                        local.Painted[key] = pixel;
                    }
                    pixels[row * width + column] = pixel;
                }
                return local;
            },
            _ => { });
    }

    /// <summary>CIE L* of a relative luminance.</summary>
    public static double Lightness(double luminance) =>
        luminance > 216d / 24389 ? 116 * Math.Cbrt(luminance) - 16 : luminance * 24389d / 27;

    /// <summary>The relative luminance of a CIE L*.</summary>
    public static double LuminanceOf(double lightness) =>
        lightness > 8 ? Math.Pow((lightness + 16) / 116, 3) : lightness * 27d / 24389;

    private Color Paint(Color color) => _paint?.Invoke(color) ?? color;

    private double SaturationAt(double x) => MinSaturation + Math.Clamp(x, 0, 1) * (MaxSaturation - MinSaturation);

    private double LuminanceAt(double y) => LuminanceOf(MaxLightness - Math.Clamp(y, 0, 1) * (MaxLightness - MinLightness));

    private static double Fraction(double value, double min, double max) => max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;

    private static double Luminance(Color color) =>
        .2126 * LinearChannel[color.R] + .7152 * LinearChannel[color.G] + .0722 * LinearChannel[color.B];

    private static double[] CreateLinearChannel()
    {
        var table = new double[256];
        for (var i = 0; i < 256; i++)
        {
            var value = i / 255d;
            table[i] = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return table;
    }

    // Every channel rises with HSL lightness at a fixed hue and saturation, so
    // luminance does too and the search over steps is a bisection.
    private static Color Step(double hue, double saturation, int step)
    {
        var lightness = step / (double)(Steps - 1);
        if (saturation <= 0)
        {
            var grey = (byte)Math.Round(lightness * 255);
            return Color.FromRgb(grey, grey, grey);
        }
        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        var h = hue / 360;
        return Color.FromRgb(Channel(p, q, h + 1d / 3), Channel(p, q, h), Channel(p, q, h - 1d / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        var value = t < 1d / 6 ? p + (q - p) * 6 * t
            : t < 0.5 ? q
            : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6
            : p;
        return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
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

/// <summary>Hue from 360° at the top to 0° at the bottom.</summary>
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
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        for (var i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(new HsvColor(1, 360 - i * 60, 1, 1).ToRgb(), i / 6.0));
        return brush;
    }

    static HueStrip()
    {
        AffectsRender<HueStrip>(HueProperty);
    }

    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }

    /// <summary>The hue at a height, clamped to the strip. Never 360, which HSV wraps to red at the bottom.</summary>
    public static double At(double y, double height) => height > 0 ? Math.Min(359.99, (1 - Math.Clamp(y / height, 0, 1)) * 360) : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        SetCurrentValue(HueProperty, At(e.GetPosition(this).Y, Bounds.Height));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Handled = true;
        SetCurrentValue(HueProperty, At(e.GetPosition(this).Y, Bounds.Height));
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
        var radius = rect.Width / 2;
        context.DrawRectangle(Hues, null, rect, radius, radius);
        var hue = Hue;
        var centre = new Point(rect.Width / 2, (1 - hue / 360) * rect.Height);
        ColorField.DrawThumb(context, centre, new SolidColorBrush(new HsvColor(1, hue, 1, 1).ToRgb()));
    }
}
