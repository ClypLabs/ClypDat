using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Controls;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReleaseNotesMarkdownTests
{
    [Fact]
    public void RendersBoldWarningWithoutMarkdownMarkers()
    {
        var textBlock = ReleaseNotesMarkdownRenderer.CreateTextBlock("**Warning:** restart ClypDat after updating.");

        Assert.Equal("Warning: restart ClypDat after updating.", textBlock.Inlines!.Text);
        var warning = Assert.IsType<Run>(textBlock.Inlines.First());
        Assert.Equal(FontWeight.Bold, warning.FontWeight);
    }

    [Fact]
    public void RendersMixedStylesCodeEscapesAndLinks()
    {
        var textBlock = ReleaseNotesMarkdownRenderer.CreateTextBlock("***both***, ~~old~~, `value`, \\*literal\\*, [site](https://clypdat.xyz)");
        var inlines = Assert.IsType<InlineCollection>(textBlock.Inlines);
        var runs = inlines.OfType<Run>().ToArray();

        Assert.Equal("both, old, value, *literal*, site", inlines.Text);
        Assert.Contains(runs, run => run.Text == "both" && run.FontWeight == FontWeight.Bold && run.FontStyle == FontStyle.Italic);
        Assert.Contains(runs, run => run.Text == "old" && run.TextDecorations!.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough));
        Assert.Contains(runs, run => run.Text == "value" && run.FontFamily.Name == "Cascadia Mono");
        Assert.Contains(runs, run => run.Text == "site" && run.TextDecorations!.Any(decoration => decoration.Location == TextDecorationLocation.Underline));
    }

    [Theory]
    [InlineData("https://clypdat.xyz", true)]
    [InlineData("HTTP://clypdat.xyz/release", true)]
    [InlineData("mailto:updates@clypdat.xyz", false)]
    [InlineData("file:///C:/setup.exe", false)]
    [InlineData("relative/release", false)]
    public void ActivatesOnlyAbsoluteHttpLinks(string destination, bool expected)
    {
        Assert.Equal(expected, ReleaseNotesMarkdownRenderer.IsSupportedLink(destination));
    }

    [Fact]
    public void KeepsHtmlAndImagesAsText()
    {
        var textBlock = ReleaseNotesMarkdownRenderer.CreateTextBlock("<b>literal</b> ![preview](https://clypdat.xyz/image.png)");

        Assert.Equal("<b>literal</b> ![preview](https://clypdat.xyz/image.png)", textBlock.Inlines!.Text);
    }

    [Fact]
    public void AutoLinksWrapAsTextRunsAndMalformedMarkersStayReadable()
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 260 };
        ReleaseNotesMarkdownRenderer.Apply(textBlock, "Read https://clypdat.xyz/releases and **unfinished");
        var inlines = Assert.IsType<InlineCollection>(textBlock.Inlines);
        var runs = inlines.OfType<Run>().ToArray();

        Assert.Equal(TextWrapping.Wrap, textBlock.TextWrapping);
        Assert.DoesNotContain(inlines, inline => inline is InlineUIContainer);
        Assert.Contains(runs, run => run.Text == "https://clypdat.xyz/releases" && run.TextDecorations!.Any(decoration => decoration.Location == TextDecorationLocation.Underline));
        Assert.Equal("Read https://clypdat.xyz/releases and **unfinished", inlines.Text);
    }

    [Fact]
    public void ExtractsAliasesMultilineBulletsAndLegacyNotes()
    {
        var body = """
            ## Features
            - First **formatted** item
              continues here
            * Second item
            ## Bug Fixes
            + Fixed `thing`
              on two lines
            """;

        var notes = AppUpdateService.ExtractCategorizedNotes(body);
        var legacy = AppUpdateService.ExtractCategorizedNotes("- Existing release note");

        Assert.Equal(["First **formatted** item\ncontinues here", "Second item"], notes.WhatsNew);
        Assert.Equal(["Fixed `thing`\non two lines"], notes.Fixes);
        Assert.Equal(["Existing release note"], legacy.WhatsNew);
        Assert.Empty(legacy.Fixes);
    }

    [Fact]
    public void PreservesEmptySectionsAndVersionPrefixes()
    {
        var notes = AppUpdateService.ExtractCategorizedNotes("## What's New\n## Fixes\n- Corrected issue");
        var prefixed = AppUpdateService.PrefixReleaseNotes(new Version(2, 4, 6), notes.WhatsNew, notes.Fixes);

        Assert.Empty(prefixed.WhatsNew);
        Assert.Equal(["2.4.6: Corrected issue"], prefixed.Fixes);
    }
}
