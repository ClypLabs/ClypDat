using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DetectorTemplateTests
{
    private static string TemplateRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "detector-templates")))
                directory = directory.Parent;
            return directory is null ? string.Empty : Path.Combine(directory.FullName, "detector-templates");
        }
    }

    private static GrayDetectorImage Gray(int width, int height, Func<int, int, byte> shade)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = shade(x, y);
        return new GrayDetectorImage(width, height, pixels);
    }

    [Fact]
    public void ManifestLoadsEveryTemplateForBothGames()
    {
        if (TemplateRoot.Length == 0) return;

        var overwatch = DetectorTemplates.Load("overwatch", DetectorRegions.ForGame("overwatch")!, TemplateRoot);
        var fortnite = DetectorTemplates.Load("fortnite", DetectorRegions.ForGame("fortnite")!, TemplateRoot);

        // A missing PNG or a bad slot silently drops the entry, so count it.
        // Eight for Overwatch: four streak tiers, Team Kill, the highlight bar
        // in both the spellings the game uses for it, and the kill cam label.
        Assert.Equal(8, overwatch.Count);
        Assert.Equal(6, fortnite.Count);
        Assert.Contains(overwatch, item => item.EventId == "team-kill");
        Assert.Equal(2, overwatch.Count(item => item.EventId == "play-of-the-game"));
        Assert.Contains(fortnite, item => item.EventId == "victory-royale");
        // Every loaded template must sit inside its slot, or the crop is empty.
        Assert.All(overwatch.Concat(fortnite), item =>
        {
            Assert.True(item.SlotRegion.Width > 0);
            Assert.True(item.SlotRegion.Height > 0);
            Assert.True(item.SlotRegion.X + item.SlotRegion.Width <= 1.0001);
            Assert.True(item.SlotRegion.Y + item.SlotRegion.Height <= 1.0001);
        });
    }

    // A template matches itself exactly, and something unrelated does not - the
    // property the whole banner path rests on.
    [Fact]
    public void CorrelationSeparatesAMatchFromNoise()
    {
        var template = Gray(40, 12, (x, y) => (byte)(x * 6 + y * 3));
        var matcher = GrayTemplateMatcher.FromGray(template);

        Assert.True(matcher.Score(template) > 0.99);
        Assert.True(matcher.Score(Gray(40, 12, (_, _) => 128)) < 0.2);
    }

    // Fortnite shifts its banner vertically when it stacks another line above,
    // so a fixed offset would miss it.
    [Fact]
    public void SlidingSearchFindsABannerThatMovedVertically()
    {
        var template = Gray(40, 10, (x, y) => (byte)(x * 6 + y * 4));
        var matcher = GrayTemplateMatcher.FromGray(template);

        // Same banner sitting 18 rows down a taller band.
        var band = Gray(40, 40, (x, y) => y is >= 18 and < 28 ? (byte)(x * 6 + (y - 18) * 4) : (byte)90);

        Assert.True(matcher.Score(band) < 0.8);
        Assert.True(matcher.ScoreBest(band) > 0.95);
    }
}
