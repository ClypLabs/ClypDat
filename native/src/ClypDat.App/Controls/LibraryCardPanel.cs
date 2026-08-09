using Avalonia;
using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

// Publishes card geometry during the same measure pass that WrapPanel uses to
// size its children. Keeping the slot width and view-model geometry together
// prevents one frame of cards using the previous viewport's width.
internal sealed class LibraryCardPanel : WrapPanel
{
    public static readonly StyledProperty<bool> ScaleWithWindowProperty =
        AvaloniaProperty.Register<LibraryCardPanel, bool>(nameof(ScaleWithWindow), true);

    public event EventHandler<LibraryCardLayout>? MetricsChanged;

    static LibraryCardPanel()
    {
        AffectsMeasure<LibraryCardPanel>(ScaleWithWindowProperty);
    }

    public bool ScaleWithWindow
    {
        get => GetValue(ScaleWithWindowProperty);
        set => SetValue(ScaleWithWindowProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Horizontal scrolling is disabled on the owning ScrollViewer, so this
        // is the finite width WrapPanel will use for the actual row packing.
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            var layout = LibraryCardLayoutCalculator.Calculate(availableSize.Width, ScaleWithWindow);
            ItemWidth = layout.Width + LibraryCardLayoutCalculator.HorizontalMargin;
            MetricsChanged?.Invoke(this, layout);
        }

        return base.MeasureOverride(availableSize);
    }
}
