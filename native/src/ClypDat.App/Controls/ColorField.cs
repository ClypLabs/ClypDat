using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ClypDat.App.Controls;

// The theme editor's colour field and hue strip, after PhotoCraft's picker:
// the colour under the pointer is a function of where the pointer is and
// nothing else. Avalonia's ColorSpectrum instead looks the colour up in a
// table it rebuilds on a background thread whenever its size, range or hue
// changes, and keeps Color and HsvColor in step by converting one into the
// other; a drag could read a stale table or a rounded round trip, so the
// value under the cursor and the value reported drifted apart.
//
// Both are drawn from two gradients rather than a bitmap. In HSV a colour at
// a fixed hue is V * (1 - S * (1 - hue)), so a horizontal saturation gradient
// with a vertical black overlay of opacity 1 - V paints the field exactly.

/// <summary>Saturation across, value up, for the hue in <see cref="HsvColor"/>.</summary>
public sealed class SaturationValueField : Control
{
    public static readonly StyledProperty<HsvColor> HsvColorProperty =
        AvaloniaProperty.Register<SaturationValueField, HsvColor>(nameof(HsvColor), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    // Percentages, as ColorSpectrum took them, so the editor's crops carry over.
    public static readonly StyledProperty<double> MinSaturationProperty =
        AvaloniaProperty.Register<SaturationValueField, double>(nameof(MinSaturation), 0);
    public static readonly StyledProperty<double> MaxSaturationProperty =
        AvaloniaProperty.Register<SaturationValueField, double>(nameof(MaxSaturation), 100);
    public static readonly StyledProperty<double> MinValueProperty =
        AvaloniaProperty.Register<SaturationValueField, double>(nameof(MinValue), 0);
    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<SaturationValueField, double>(nameof(MaxValue), 100);
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<SaturationValueField, double>(nameof(CornerRadius), 8);

    static SaturationValueField()
    {
        AffectsRender<SaturationValueField>(HsvColorProperty, MinSaturationProperty, MaxSaturationProperty, MinValueProperty, MaxValueProperty, CornerRadiusProperty);
    }

    public SaturationValueField() => Cursor = new Cursor(StandardCursorType.Cross);

    public HsvColor HsvColor { get => GetValue(HsvColorProperty); set => SetValue(HsvColorProperty, value); }
    public double MinSaturation { get => GetValue(MinSaturationProperty); set => SetValue(MinSaturationProperty, value); }
    public double MaxSaturation { get => GetValue(MaxSaturationProperty); set => SetValue(MaxSaturationProperty, value); }
    public double MinValue { get => GetValue(MinValueProperty); set => SetValue(MinValueProperty, value); }
    public double MaxValue { get => GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The saturation and value at a point, clamped to the field.</summary>
    public static (double S, double V) At(Point point, Size size, double minS, double maxS, double minV, double maxV)
    {
        var x = size.Width > 0 ? Math.Clamp(point.X / size.Width, 0, 1) : 0;
        var y = size.Height > 0 ? Math.Clamp(point.Y / size.Height, 0, 1) : 0;
        return ((minS + x * (maxS - minS)) / 100, (maxV - y * (maxV - minV)) / 100);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Captured, so a drag that leaves the field keeps steering the colour
        // along its edge instead of stopping where the pointer crossed it.
        e.Pointer.Capture(this);
        e.Handled = true;
        Pick(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Handled = true;
        Pick(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    private void Pick(Point point)
    {
        var (s, v) = At(point, Bounds.Size, MinSaturation, MaxSaturation, MinValue, MaxValue);
        var current = HsvColor;
        SetCurrentValue(HsvColorProperty, new HsvColor(current.A, current.H, s, v));
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var hue = HsvColor.H;
        var saturation = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(new HsvColor(1, hue, MinSaturation / 100, 1).ToRgb(), 0),
                new GradientStop(new HsvColor(1, hue, MaxSaturation / 100, 1).ToRgb(), 1),
            },
        };
        var value = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(Alpha(1 - MaxValue / 100), 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(Alpha(1 - MinValue / 100), 0, 0, 0), 1),
            },
        };
        context.DrawRectangle(saturation, null, rect, CornerRadius, CornerRadius);
        context.DrawRectangle(value, null, rect, CornerRadius, CornerRadius);

        var color = HsvColor;
        var x = Fraction(color.S * 100, MinSaturation, MaxSaturation) * rect.Width;
        var y = (1 - Fraction(color.V * 100, MinValue, MaxValue)) * rect.Height;
        DrawThumb(context, new Point(x, y), null);
    }

    private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    private static double Fraction(double value, double min, double max) => max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;

    internal static void DrawThumb(DrawingContext context, Point centre, IBrush? fill)
    {
        // White ring inside a dark one, so it reads on every colour the field
        // can show without switching between light and dark selectors.
        context.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 4), centre, 7, 7);
        context.DrawEllipse(null, new Pen(Brushes.White, 2), centre, 7, 7);
    }
}

/// <summary>Hue from 360° at the top to 0° at the bottom, as in Photoshop and PhotoCraft.</summary>
public sealed class HueStrip : Control
{
    public static readonly StyledProperty<HsvColor> HsvColorProperty =
        AvaloniaProperty.Register<HueStrip, HsvColor>(nameof(HsvColor), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

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
        AffectsRender<HueStrip>(HsvColorProperty);
    }

    public HsvColor HsvColor { get => GetValue(HsvColorProperty); set => SetValue(HsvColorProperty, value); }

    /// <summary>The hue at a height, clamped to the strip. Never 360, which HSV wraps to red at the bottom.</summary>
    public static double At(double y, double height) => height > 0 ? Math.Min(359.99, (1 - Math.Clamp(y / height, 0, 1)) * 360) : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        Pick(e.GetPosition(this).Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Handled = true;
        Pick(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    private void Pick(double y)
    {
        var current = HsvColor;
        SetCurrentValue(HsvColorProperty, new HsvColor(current.A, At(y, Bounds.Height), current.S, current.V));
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = rect.Width / 2;
        context.DrawRectangle(Hues, null, rect, radius, radius);
        var hue = HsvColor.H;
        var centre = new Point(rect.Width / 2, (1 - hue / 360) * rect.Height);
        SaturationValueField.DrawThumb(context, centre, new SolidColorBrush(new HsvColor(1, hue, 1, 1).ToRgb()));
    }
}
