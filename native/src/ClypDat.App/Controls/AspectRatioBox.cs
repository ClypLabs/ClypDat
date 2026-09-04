using Avalonia;
using Avalonia.Controls;

namespace ClypDat.App.Controls;

internal sealed class AspectRatioBox : Decorator
{
    public static readonly StyledProperty<double> AspectRatioProperty = AvaloniaProperty.Register<AspectRatioBox, double>(nameof(AspectRatio), 16d / 9d);
    static AspectRatioBox() => AffectsMeasure<AspectRatioBox>(AspectRatioProperty);
    public double AspectRatio { get => GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 0;
        var size = new Size(width, width / Math.Max(0.001, AspectRatio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(finalSize.Width, finalSize.Width / Math.Max(0.001, AspectRatio));
        Child?.Arrange(new Rect(size));
        return size;
    }
}
