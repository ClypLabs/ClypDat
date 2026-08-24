using Avalonia;
using Avalonia.Controls;

namespace ClypDat.App.Controls;

// Keeps a page in layout while it is covered by another page. Opacity avoids
// rendering it, while disabled/hit-test-invisible state keeps it inert.
internal sealed class RetainedPageHost : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<RetainedPageHost, bool>(nameof(IsActive), true);

    static RetainedPageHost()
    {
        IsActiveProperty.Changed.AddClassHandler<RetainedPageHost>((host, _) => host.ApplyActiveState());
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyActiveState();
    }

    private void ApplyActiveState()
    {
        var active = IsActive;
        Opacity = active ? 1 : 0;
        IsHitTestVisible = active;
        IsEnabled = active;
    }
}
