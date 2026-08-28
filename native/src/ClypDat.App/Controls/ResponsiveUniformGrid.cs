using System;
using Avalonia;
using Avalonia.Controls;

namespace ClypDat.App.Controls;

/// <summary>
/// A compact preset grid that keeps cards readable by dropping directly from
/// four columns to two when the host becomes narrow.
/// </summary>
internal sealed class ResponsiveUniformGrid : Panel
{
    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<ResponsiveUniformGrid, double>(nameof(MinimumItemWidth), 180);

    public static readonly StyledProperty<int> MaximumColumnsProperty =
        AvaloniaProperty.Register<ResponsiveUniformGrid, int>(nameof(MaximumColumns), 4);

    static ResponsiveUniformGrid()
    {
        AffectsMeasure<ResponsiveUniformGrid>(MinimumItemWidthProperty, MaximumColumnsProperty);
    }

    public double MinimumItemWidth
    {
        get => GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public int MaximumColumns
    {
        get => GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = GetColumnCount(availableSize.Width);
        var cellWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width / columns
            : double.PositiveInfinity;
        var maxWidth = 0d;
        var totalHeight = 0d;
        var rowHeight = 0d;

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            maxWidth = Math.Max(maxWidth, child.DesiredSize.Width);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            if ((index + 1) % columns == 0 || index == Children.Count - 1)
            {
                totalHeight += rowHeight;
                rowHeight = 0;
            }
        }

        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : maxWidth * columns, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = GetColumnCount(finalSize.Width);
        var cellWidth = finalSize.Width / columns;
        var rowCount = (Children.Count + columns - 1) / columns;
        var rowHeights = new double[rowCount];

        for (var index = 0; index < Children.Count; index++)
            rowHeights[index / columns] = Math.Max(rowHeights[index / columns], Children[index].DesiredSize.Height);

        var top = 0d;
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= Children.Count) break;
                Children[index].Arrange(new Rect(column * cellWidth, top, cellWidth, rowHeights[row]));
            }

            top += rowHeights[row];
        }

        return finalSize;
    }

    private int GetColumnCount(double width)
    {
        var maximum = Math.Max(1, MaximumColumns);
        if (!double.IsFinite(width) || width <= 0) return maximum;

        var fittingColumns = (int)Math.Floor(width / Math.Max(1, MinimumItemWidth));
        if (fittingColumns >= maximum) return maximum;
        return fittingColumns >= 2 ? 2 : 1;
    }
}
