using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClypDat.App.Services;
using Markdig;
using Markdig.Syntax;

namespace ClypDat.App.Views;

// THIRD-PARTY-LICENSES.md laid out inside the app: headings, paragraphs,
// bullet lists and rules, with inline formatting and http(s) links handled
// by the same renderer the release notes use. Anything else in the file is
// shown as plain text rather than dropped - it is a legal notice, and a
// missing line is worse than an unformatted one.
internal static class LicenceDocumentView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    internal static Control Build(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var column = new StackPanel { Spacing = 10 };
        foreach (var block in document)
        {
            if (Render(block, markdown) is { } control) column.Children.Add(control);
        }
        return column;
    }

    private static Control? Render(Block block, string source)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return new TextBlock
                {
                    Text = Source(heading.Span, source).TrimStart('#').Trim(),
                    Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB"),
                    FontSize = heading.Level <= 1 ? 20 : 15.5,
                    FontWeight = heading.Level <= 1 ? FontWeight.Bold : FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = heading.Level <= 1 ? new Thickness(0, 0, 0, 2) : new Thickness(0, 14, 0, 0)
                };

            case ParagraphBlock paragraph:
                return Paragraph(Source(paragraph.Span, source));

            case ListBlock list:
                var items = new StackPanel { Spacing = 6 };
                foreach (var entry in list)
                {
                    if (entry is not ListItemBlock item) continue;
                    var text = string.Join(" ", item.OfType<LeafBlock>().Select(leaf => Source(leaf.Span, source)));
                    var bullet = new Border
                    {
                        Width = 5,
                        Height = 5,
                        CornerRadius = new CornerRadius(2.5),
                        Background = AppThemeService.Brush("Text_6B7C8C", "#6B7C8C"),
                        Margin = new Thickness(2, 8, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
                    var body = Paragraph(text);
                    Grid.SetColumn(body, 1);
                    line.Children.Add(bullet);
                    line.Children.Add(body);
                    items.Children.Add(line);
                }
                return items;

            case ThematicBreakBlock:
                return new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8),
                    Background = AppThemeService.Brush("Surface_1B242D", "#1B242D")
                };

            case LeafBlock leaf:
                return Paragraph(Source(leaf.Span, source));

            case ContainerBlock container:
                var nested = new StackPanel { Spacing = 10 };
                foreach (var child in container)
                {
                    if (Render(child, source) is { } control) nested.Children.Add(control);
                }
                return nested;

            default:
                return null;
        }
    }

    // The file is hard-wrapped at ~80 columns for the repository; in the
    // dialog a paragraph should flow to the width it has, so its source lines
    // are joined before rendering.
    private static TextBlock Paragraph(string markdown)
    {
        markdown = string.Join(" ", markdown.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
        var text = new TextBlock
        {
            Foreground = AppThemeService.Brush("Text_B9C6D4", "#B9C6D4"),
            FontSize = 13.5,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        };
        ReleaseNotesMarkdownRenderer.Apply(text, markdown);
        return text;
    }

    private static string Source(SourceSpan span, string source) =>
        span.IsEmpty || span.Start < 0 || span.End >= source.Length
            ? string.Empty
            : source.Substring(span.Start, span.Length).Replace("\r", string.Empty).Trim();
}
