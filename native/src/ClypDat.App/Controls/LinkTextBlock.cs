using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClypDat.App.Services;

namespace ClypDat.App.Controls;

// A paragraph with web links in it. Links are ordinary Runs carrying
// LinkTextBlock.Url, so they share the paragraph's font, size and baseline -
// a Button in an InlineUIContainer brought Fluent's own font size and sat
// above the line. Clicks are hit-tested against the text layout.
public sealed class LinkTextBlock : TextBlock
{
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<LinkTextBlock, Run, string?>("Url");

    public static string? GetUrl(Run run) => run.GetValue(UrlProperty);
    public static void SetUrl(Run run, string? value) => run.SetValue(UrlProperty, value);

    private static readonly IBrush LinkBrush = new ImmutableSolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush LinkHoverBrush = new ImmutableSolidColorBrush(Color.Parse("#7DD3FC"));

    private Run? _hovered;

    // Without a background only the glyphs are hit-testable, and the gaps
    // between letters would drop the hover.
    public LinkTextBlock() => Background = Brushes.Transparent;

    // Keeps TextBlock.settingsRowDesc and friends applying to this control.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        foreach (var run in LinkRuns()) run.Foreground = LinkBrush;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovered(RunAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovered(null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (RunAt(e.GetPosition(this)) is not { } run || GetUrl(run) is not { Length: > 0 } url) return;
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error($"Opening {url} failed", error);
        }
    }

    private void SetHovered(Run? run)
    {
        if (ReferenceEquals(run, _hovered)) return;
        if (_hovered is not null)
        {
            _hovered.Foreground = LinkBrush;
            _hovered.TextDecorations = null;
        }
        _hovered = run;
        if (run is not null)
        {
            run.Foreground = LinkHoverBrush;
            run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        }
        Cursor = run is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
    }

    private IEnumerable<Run> LinkRuns() =>
        Inlines?.OfType<Run>().Where(run => !string.IsNullOrEmpty(GetUrl(run))) ?? [];

    // Maps a point to the link Run under it. The layout is built from the
    // Runs in order, so a character index walks the Runs' text lengths.
    private Run? RunAt(Point point)
    {
        if (Inlines is null || Inlines.Count == 0) return null;
        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = TextLayout.HitTestPoint(local);
        if (!hit.IsInside) return null;
        var index = hit.TextPosition;
        var start = 0;
        foreach (var inline in Inlines)
        {
            var length = inline is Run run ? run.Text?.Length ?? 0 : 1;
            if (index < start + length) return inline is Run link && !string.IsNullOrEmpty(GetUrl(link)) ? link : null;
            start += length;
        }
        return null;
    }
}
