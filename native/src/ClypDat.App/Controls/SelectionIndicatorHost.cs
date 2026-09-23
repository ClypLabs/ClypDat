using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ClypDat.App.Controls;

/// <summary>
/// The panel inside the SlidingSelectionListBox template (AppStyles.axaml):
/// its <c>PART_SelectionIndicator</c> child is laid over whichever item of
/// the templated ListBox is selected, and glides from the old item to the new
/// one when the selection changes, instead of one highlight switching off and
/// another switching on. Each option list styles the indicator itself - a
/// filled pill under segmented choices, an accent ring over cards and
/// swatches - and its ZIndex decides whether it sits under or over the items.
/// </summary>
/// <remarks>
/// The indicator is arranged here directly, frame by frame, rather than
/// animated through Margin/Width transitions: first placement and layout
/// changes (a resize, a wrap panel reflowing) have to jump straight to the
/// item, and only a change of selection should move.
/// </remarks>
public sealed class SelectionIndicatorHost : Panel
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

    private ListBox? _list;
    private Control? _indicator;
    private Rect _current;
    private Rect _from;
    private Rect _target;
    private bool _placed;
    private bool _slideOnNextArrange;
    private bool _sliding;
    private bool _frameRequested;
    private TimeSpan _slideStarted;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _list = TemplatedParent as ListBox;
        if (_list is not null) _list.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_list is not null) _list.SelectionChanged -= OnSelectionChanged;
        _list = null;
        _placed = false;
        _sliding = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _slideOnNextArrange = true;
        InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _indicator ??= FindIndicator();
        foreach (var child in Children)
        {
            if (!ReferenceEquals(child, _indicator)) child.Arrange(new Rect(finalSize));
        }
        if (_indicator is null) return finalSize;

        if (SelectedItemRect() is not { } target)
        {
            _placed = false;
            _sliding = false;
            _slideOnNextArrange = false;
            _indicator.Opacity = 0;
            _indicator.Arrange(default);
            return finalSize;
        }

        if (!_placed)
        {
            _current = _target = target;
            _placed = true;
        }
        else if (target != _target)
        {
            if (_slideOnNextArrange)
            {
                _from = _current;
                _target = target;
                _slideStarted = _clock.Elapsed;
                _sliding = true;
                RequestFrame();
            }
            else if (_sliding)
            {
                // The destination moved under a slide already under way (the
                // list reflowed); keep gliding, just to the new spot.
                _target = target;
            }
            else
            {
                _current = _target = target;
            }
        }
        _slideOnNextArrange = false;

        _indicator.Opacity = 1;
        _indicator.Arrange(_current);
        return finalSize;
    }

    private Control? FindIndicator()
    {
        foreach (var child in Children)
        {
            if (child.Name == "PART_SelectionIndicator") return child;
        }
        return null;
    }

    private Rect? SelectedItemRect()
    {
        if (_list is not { SelectedIndex: >= 0 } list) return null;
        if (list.ContainerFromIndex(list.SelectedIndex) is not { IsVisible: true } container) return null;
        if (container.TranslatePoint(default, this) is not { } origin) return null;
        return new Rect(origin, container.Bounds.Size);
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            _current = _target;
            _sliding = false;
            return;
        }
        _frameRequested = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameRequested = false;
        if (!_sliding) return;
        var progress = Math.Clamp((_clock.Elapsed - _slideStarted) / SlideDuration, 0, 1);
        // Cubic ease-out: quick to leave, soft to land.
        var eased = 1 - Math.Pow(1 - progress, 3);
        _current = new Rect(
            Lerp(_from.X, _target.X, eased),
            Lerp(_from.Y, _target.Y, eased),
            Lerp(_from.Width, _target.Width, eased),
            Lerp(_from.Height, _target.Height, eased));
        if (progress >= 1)
        {
            _current = _target;
            _sliding = false;
        }
        InvalidateArrange();
        if (_sliding) RequestFrame();
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
}
