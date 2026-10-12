using Avalonia;
using Avalonia.Controls.Documents;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LinkTextBlockTests
{
    [Fact]
    public void PointOverLinkTextFindsTheLinkRun()
    {
        AvaloniaTestThread.Run(() =>
        {
            var link = new Run("setup guide");
            LinkTextBlock.SetUrl(link, "https://www.clypdat.xyz/docs/cs2-match-stats");
            var text = new LinkTextBlock { FontSize = 12, LineHeight = 17, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            text.Inlines!.Add(new Run("New to this? Follow the "));
            text.Inlines.Add(link);
            text.Inlines.Add(new Run("."));
            text.Measure(new Size(600, double.PositiveInfinity));
            text.Arrange(new Rect(text.DesiredSize));

            var linkStart = "New to this? Follow the ".Length;
            var over = text.TextLayout.HitTestTextPosition(linkStart + 3).Center;
            var before = text.TextLayout.HitTestTextPosition(3).Center;

            Assert.Same(link, text.RunAt(over));
            Assert.Null(text.RunAt(before));
        }, TimeSpan.FromSeconds(10), "Link hit test timed out.");
    }
}
