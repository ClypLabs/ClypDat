using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Controls;
using Avalonia;
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
    public void InheritsParentForegroundKeepsLinksAccentedAndWrapsLongLabels()
    {
        AvaloniaTestThread.Run(() =>
        {
            var initialBrush = new SolidColorBrush(Colors.OrangeRed);
            var changedBrush = new SolidColorBrush(Colors.MediumPurple);
            var textBlock = new TextBlock
            {
                Foreground = initialBrush,
                TextWrapping = TextWrapping.Wrap,
                Width = 260
            };
            ReleaseNotesMarkdownRenderer.Apply(textBlock,
                "plain **bold** *italic* ~~strike~~ `code` [this unsupported release note label wraps at the existing dialog width](mailto:updates@clypdat.xyz) [supported link](https://clypdat.xyz/releases)");

            var runs = textBlock.Inlines!.OfType<Run>().ToArray();
            var supportedLink = Assert.Single(runs, run => run.Text == "supported link");
            var ordinaryRuns = runs.Where(run => run != supportedLink).ToArray();
            var unsupportedLabel = Assert.Single(ordinaryRuns, run => run.Text == "this unsupported release note label wraps at the existing dialog width");

            Assert.NotEmpty(ordinaryRuns);
            Assert.All(ordinaryRuns, run => Assert.Same(initialBrush, run.Foreground));
            Assert.DoesNotContain(unsupportedLabel.TextDecorations ?? [], decoration => decoration.Location == TextDecorationLocation.Underline);
            Assert.Equal(Color.FromRgb(0x71, 0xD7, 0xFF), Assert.IsAssignableFrom<ISolidColorBrush>(supportedLink.Foreground).Color);

            textBlock.Foreground = changedBrush;

            Assert.All(ordinaryRuns, run => Assert.Same(changedBrush, run.Foreground));
            Assert.Equal(Color.FromRgb(0x71, 0xD7, 0xFF), Assert.IsAssignableFrom<ISolidColorBrush>(supportedLink.Foreground).Color);

            textBlock.Measure(new Size(260, double.PositiveInfinity));
            textBlock.Arrange(new Rect(0, 0, 260, textBlock.DesiredSize.Height));
            var layout = textBlock.TextLayout;
            var drawableRuns = layout.TextLines.SelectMany(line => line.TextRuns).OfType<DrawableTextRun>().ToArray();

            Assert.True(layout.TextLines.Count > 1, "Long unsupported labels must wrap at the dialog's 260px text width.");
            Assert.NotEmpty(drawableRuns);
            Assert.All(drawableRuns, run => Assert.NotNull(run.Properties!.ForegroundBrush));
        }, TimeSpan.FromSeconds(30), "Release note text layout timed out.");
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
