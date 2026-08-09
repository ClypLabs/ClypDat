using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

// Publishes card geometry during same measure pass used to pack children.
// Keeps containers live; unmatched clips take zero layout space.
internal sealed class LibraryCardPanel : Panel
{
    public static readonly StyledProperty<bool> ScaleWithWindowProperty =
        AvaloniaProperty.Register<LibraryCardPanel, bool>(nameof(ScaleWithWindow), true);

    public static readonly StyledProperty<double> ReservedHeightProperty =
        AvaloniaProperty.Register<LibraryCardPanel, double>(nameof(ReservedHeight));

    public event EventHandler<LibraryCardLayout>? MetricsChanged;

    static LibraryCardPanel()
    {
        AffectsMeasure<LibraryCardPanel>(ScaleWithWindowProperty);
        AffectsMeasure<LibraryCardPanel>(ReservedHeightProperty);
    }

    public bool ScaleWithWindow
    {
        get => GetValue(ScaleWithWindowProperty);
        set => SetValue(ScaleWithWindowProperty, value);
    }

    public double ReservedHeight
    {
        get => GetValue(ReservedHeightProperty);
        set => SetValue(ReservedHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(IsClipVisible).ToArray();
        var reservedHeight = double.IsFinite(ReservedHeight) ? Math.Max(0, ReservedHeight) : 0;

        if (!double.IsFinite(availableSize.Width) || availableSize.Width <= 0)
        {
            foreach (var child in Children)
            {
                if (IsClipVisible(child)) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                else child.Measure(new Size(0, 0));
            }
            return new Size(0, Math.Max(reservedHeight, visible.Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max()));
        }

        var layout = LibraryCardLayoutCalculator.Calculate(availableSize.Width, ScaleWithWindow);
        MetricsChanged?.Invoke(this, layout);

        var slotWidth = LibraryCardLayoutCalculator.SlotWidth(layout.Width);
        foreach (var child in Children)
        {
            if (IsClipVisible(child)) child.Measure(new Size(slotWidth, double.PositiveInfinity));
            else child.Measure(new Size(0, 0));
        }

        var height = 0d;
        for (var index = 0; index < visible.Length; index += layout.Columns)
        {
            height += visible.Skip(index).Take(layout.Columns).Max(child => child.DesiredSize.Height);
        }

        return new Size(Math.Min(availableSize.Width, slotWidth * layout.Columns), Math.Max(height, reservedHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!double.IsFinite(finalSize.Width) || finalSize.Width <= 0)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        var layout = LibraryCardLayoutCalculator.Calculate(finalSize.Width, ScaleWithWindow);
        var slotWidth = LibraryCardLayoutCalculator.SlotWidth(layout.Width);
        var visible = Children.Where(IsClipVisible).ToArray();
        var y = 0d;
        for (var index = 0; index < visible.Length; index += layout.Columns)
        {
            var row = visible.Skip(index).Take(layout.Columns).ToArray();
            var rowHeight = row.Max(child => child.DesiredSize.Height);
            for (var column = 0; column < row.Length; column++)
            {
                row[column].Arrange(new Rect(column * slotWidth, y, slotWidth, rowHeight));
            }
            y += rowHeight;
        }

        foreach (var child in Children.Where(child => !IsClipVisible(child))) child.Arrange(new Rect(0, 0, 0, 0));
        var reservedHeight = double.IsFinite(ReservedHeight) ? Math.Max(0, ReservedHeight) : 0;
        return new Size(finalSize.Width, Math.Max(y, reservedHeight));
    }

    private static bool IsClipVisible(Control child)
    {
        var clip = child.DataContext as ClipCardViewModel
            ?? child.GetVisualDescendants().OfType<Control>()
                .Select(descendant => descendant.DataContext)
                .OfType<ClipCardViewModel>()
                .FirstOrDefault();
        return clip?.IsVisibleInLibrary ?? child.IsVisible;
    }
}
