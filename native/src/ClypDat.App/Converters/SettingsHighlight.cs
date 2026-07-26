using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ClypDat.App.Converters;

// Attached property: <TextBlock Text="Resolution" cv:SettingsHighlight.Query="{Binding SettingsSearchText}" />.
// Highlighting the WHOLE label whenever any part of it matched (the first
// version of this) read as garbage - typing "Res" lit up all of
// "Resolution" instead of just the "Res" the user actually typed, and typing
// further ("Reso") didn't visibly narrow anything. A TextBlock can't
// partially colour its own Text - only its Inlines (a run collection) can
// mix styled and unstyled spans - so this splits the label's own Text into
// up to three Runs (before/match/after) on every query change instead, and
// only the matched Run gets the accent background. Text itself is left
// alone (Inlines takes over rendering once populated, but doesn't erase the
// Text property), so this can safely re-read Text as the source of truth on
// every query change without needing anywhere else to remember the
// original label.
public static class SettingsHighlight
{
    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Query", typeof(SettingsHighlight));

    static SettingsHighlight()
    {
        QueryProperty.Changed.AddClassHandler<TextBlock>((textBlock, _) => Apply(textBlock));
    }

    public static void SetQuery(TextBlock textBlock, string? value) => textBlock.SetValue(QueryProperty, value);
    public static string? GetQuery(TextBlock textBlock) => textBlock.GetValue(QueryProperty);

    private static void Apply(TextBlock textBlock)
    {
        var text = textBlock.Text ?? string.Empty;
        var query = (GetQuery(textBlock) ?? string.Empty).Trim();

        if (query.Length == 0 || text.Length == 0)
        {
            textBlock.Inlines?.Clear();
            return;
        }

        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            textBlock.Inlines?.Clear();
            return;
        }

        var inlines = new InlineCollection();
        if (index > 0) inlines.Add(new Run(text[..index]));
        inlines.Add(new Run(text.Substring(index, query.Length))
        {
            Background = (IBrush?)Application.Current?.FindResource("AccentBrush") ?? Brushes.DodgerBlue,
            Foreground = Brushes.White
        });
        var after = index + query.Length;
        if (after < text.Length) inlines.Add(new Run(text[after..]));

        textBlock.Inlines = inlines;
    }
}
