using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

/// <summary>The editor's Stats section. DataContext is a <see cref="ViewModels.MatchStatsViewModel"/>.</summary>
public sealed partial class MatchStatsPanel : UserControl
{
    private InputElement? _pointerRoot;
    public MatchStatsPanel()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pointerRoot = TopLevel.GetTopLevel(this);
        _pointerRoot?.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pointerRoot?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
        _pointerRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e) =>
        DismissRadarSelectionAt(e.GetPosition(PerformanceRadar));

    internal void DismissRadarSelectionAt(Point point)
    {
        if (DataContext is MatchStatsViewModel { SelectedRadarAxis: not null } model &&
            !PerformanceRadar.LabelBounds.Any(bounds => bounds.Contains(point)))
            model.SelectedRadarAxis = null;
    }
}
