using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClypDat.App.Services;

// Release notes are remote, user-visible content. Parse only inline Markdown
// into Avalonia runs; never hand release-body HTML to an HTML renderer or a
// browser. Link targets need an explicit http/https allowlist before they can
// be opened by the shell.
internal static class ReleaseNotesMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    private static readonly IBrush LinkBrush = Brush.Parse("#71D7FF");

    internal static TextBlock CreateTextBlock(string markdown)
    {
        var textBlock = new TextBlock();
        Apply(textBlock, markdown);
        return textBlock;
    }

    internal static void Apply(TextBlock textBlock, string? markdown)
    {
        var builder = new InlineBuilder();
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var block in document)
        {
            if (block is LeafBlock leaf && leaf.Inline is not null)
            {
                builder.RenderChildren(leaf.Inline, default);
            }
            else if (block is LeafBlock unsupported)
            {
                // Full Markdown blocks are intentionally excluded. Keep their
                // source visible instead of silently discarding release text.
                builder.AddText(unsupported.Lines.ToString(), default);
            }
        }

        textBlock.Inlines = builder.Inlines;
        if (builder.Links.Count == 0)
        {
            return;
        }

        textBlock.PointerReleased += (_, eventArgs) =>
        {
            if (eventArgs.InitialPressMouseButton != MouseButton.Left)
            {
                return;
            }

            if (TryGetLinkAt(textBlock, builder.Links, eventArgs.GetPosition(textBlock), out var uri))
            {
                OpenInDefaultBrowser(uri);
                eventArgs.Handled = true;
            }
        };

        textBlock.PointerMoved += (_, eventArgs) =>
        {
            var isLink = TryGetLinkAt(textBlock, builder.Links, eventArgs.GetPosition(textBlock), out var ignoredUri);
            textBlock.Cursor = isLink
                ? new Cursor(StandardCursorType.Hand)
                : null;
        };
    }

    internal static bool IsSupportedLink(string? destination) =>
        Uri.TryCreate(destination, UriKind.Absolute, out var uri) && IsSupportedLink(uri);

    private static bool IsSupportedLink(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetLinkAt(TextBlock textBlock, IReadOnlyList<LinkTarget> links, Point point, out Uri uri)
    {
        var localPoint = point - new Point(textBlock.Padding.Left, textBlock.Padding.Top);
        var hit = textBlock.TextLayout.HitTestPoint(localPoint);
        var position = hit.TextPosition - (hit.IsTrailing ? 1 : 0);

        foreach (var link in links)
        {
            if (hit.IsInside && position >= link.Start && position < link.Start + link.Length)
            {
                uri = link.Uri;
                return true;
            }
        }

        uri = null!;
        return false;
    }

    private static void OpenInDefaultBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // A launch failure must not close or destabilize the update dialog.
        }
    }

    private sealed class InlineBuilder
    {
        internal InlineCollection Inlines { get; } = new();
        internal List<LinkTarget> Links { get; } = [];
        private int _textLength;

        internal void RenderChildren(ContainerInline container, InlineStyle style)
        {
            for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            {
                Render(child, style);
            }
        }

        internal void AddText(string? text, InlineStyle style)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var decorations = CreateDecorations(style);
            Inlines.Add(new Run(text)
            {
                FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
                FontFamily = style.Code ? new FontFamily("Cascadia Mono") : FontFamily.Default,
                Foreground = style.LinkUri is null ? null : LinkBrush,
                TextDecorations = decorations
            });
            _textLength += text.Length;
        }

        private void Render(Markdig.Syntax.Inlines.Inline inline, InlineStyle style)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(literal.Content.ToString(), style);
                    break;

                case CodeInline code:
                    AddText(code.Content, style with { Code = true });
                    break;

                case LineBreakInline:
                    AddText("\n", style);
                    break;

                case HtmlInline html:
                    AddText(html.Tag, style);
                    break;

                case HtmlEntityInline entity:
                    AddText(entity.Original.ToString(), style);
                    break;

                case EmphasisInline emphasis:
                    RenderChildren(emphasis, style with
                    {
                        Bold = style.Bold || emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2,
                        Italic = style.Italic || emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount % 2 == 1,
                        Strikethrough = style.Strikethrough || emphasis.DelimiterChar == '~'
                    });
                    break;

                case LinkInline link when link.IsImage:
                    AddText("![", style);
                    RenderChildren(link, style);
                    AddText($"]({link.Url})", style);
                    break;

                case LinkInline link:
                    RenderLink(link, style);
                    break;

                case AutolinkInline autoLink:
                    RenderAutoLink(autoLink, style);
                    break;

                case ContainerInline container:
                    RenderChildren(container, style);
                    break;
            }
        }

        private void RenderLink(LinkInline link, InlineStyle style)
        {
            var uri = IsSupportedLink(link.Url) && Uri.TryCreate(link.Url, UriKind.Absolute, out var parsed) ? parsed : null;
            var start = _textLength;
            RenderChildren(link, uri is null ? style : style with { LinkUri = uri });
            AddLink(start, uri);
        }

        private void RenderAutoLink(AutolinkInline autoLink, InlineStyle style)
        {
            var uri = IsSupportedLink(autoLink.Url) && Uri.TryCreate(autoLink.Url, UriKind.Absolute, out var parsed) ? parsed : null;
            var start = _textLength;
            AddText(autoLink.Url, uri is null ? style : style with { LinkUri = uri });
            AddLink(start, uri);
        }

        private void AddLink(int start, Uri? uri)
        {
            if (uri is not null && _textLength > start)
            {
                Links.Add(new LinkTarget(start, _textLength - start, uri));
            }
        }

        private static TextDecorationCollection? CreateDecorations(InlineStyle style)
        {
            if (!style.Strikethrough && style.LinkUri is null)
            {
                return null;
            }

            var decorations = new TextDecorationCollection();
            if (style.Strikethrough)
            {
                decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            }

            if (style.LinkUri is not null)
            {
                decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            }

            return decorations;
        }
    }

    private readonly record struct InlineStyle(bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Code = false, Uri? LinkUri = null);
    private sealed record LinkTarget(int Start, int Length, Uri Uri);
}
