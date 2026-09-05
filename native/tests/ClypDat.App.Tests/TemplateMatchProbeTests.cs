using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// End-to-end: a captured frame -> the three HUD slots -> template matching,
// exactly as the live detector does it, against frames whose answers are known.
public sealed class TemplateMatchProbeTests
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

    private static DetectorFrameSnapshot ToFrame(string path, DetectorRegionSet regions)
    {
        var full = GrayPng.Read(path);
        return new DetectorFrameSnapshot(
            DateTime.UtcNow,
            GrayTemplateMatcher.Crop(full, regions.First),
            GrayTemplateMatcher.Crop(full, regions.Second),
            GrayTemplateMatcher.Crop(full, regions.Third));
    }

    [Fact]
    public void ProbeRealFramesThroughTheFullSlotPipeline()
    {
        var folder = Environment.GetEnvironmentVariable("CLYPDAT_FRAME_PROBE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || TemplateRoot.Length == 0) return;

        var report = new List<string>();
        foreach (var (game, prefix) in new[] { ("overwatch", "ow-"), ("fortnite", "fn-") })
        {
            var regions = DetectorRegions.ForGame(game)!;
            var templates = DetectorTemplates.Load(game, regions, TemplateRoot);
            report.Add($"--- {game}: {templates.Count} templates ---");
            foreach (var file in Directory.EnumerateFiles(folder, prefix + "*.png").OrderBy(item => item))
            {
                var frame = ToFrame(file, regions);
                var scored = templates
                    .Select(template => (template.EventId, Score: template.Matcher.ScoreBest(
                        GrayTemplateMatcher.Crop(
                            template.Slot == 0 ? frame.First : template.Slot == 1 ? frame.Second : frame.Third,
                            template.SlotRegion))))
                    .OrderByDescending(item => item.Score)
                    .Take(3);
                report.Add($"{Path.GetFileName(file),-28} {string.Join("  ", scored.Select(item => $"{item.EventId}={item.Score:F3}"))}");
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "clypdat-frame-probe.txt"), report);
        Assert.NotEmpty(report);
    }

    // Known-answer frames drive the whole pipeline: crop the three slots the way
    // the live detector does, match, and check the right banner wins. These are
    // the numbers that justify the thresholds in templates.json.
    [Theory]
    [InlineData("overwatch", "ow-double.png", "double-kill")]
    [InlineData("overwatch", "ow-triple.png", "triple-kill")]
    [InlineData("overwatch", "ow-quintuple.png", "quintuple-kill")]
    [InlineData("overwatch", "ow-teamkill.png", "team-kill")]
    [InlineData("fortnite", "fn-double.png", "double-elimination")]
    [InlineData("fortnite", "fn-victory.png", "victory-royale")]
    [InlineData("fortnite", "fn-eliminatedby.png", "got-eliminated")]
    public void TheRightBannerWinsOnAKnownFrame(string game, string file, string expected)
    {
        var folder = Environment.GetEnvironmentVariable("CLYPDAT_FRAME_PROBE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, file)) || TemplateRoot.Length == 0) return;

        var regions = DetectorRegions.ForGame(game)!;
        var templates = DetectorTemplates.Load(game, regions, TemplateRoot);
        var hits = DetectorTemplates.Match(templates, ToFrame(Path.Combine(folder, file), regions));

        Assert.NotEmpty(hits);
        Assert.Equal(expected, hits[0].Template.EventId);
    }

    // A frame with no banner must produce nothing, or every clip is a false one.
    [Theory]
    [InlineData("overwatch", "ow-none.png")]
    [InlineData("fortnite", "fn-none.png")]
    public void AFrameWithNoBannerMatchesNothing(string game, string file)
    {
        var folder = Environment.GetEnvironmentVariable("CLYPDAT_FRAME_PROBE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, file)) || TemplateRoot.Length == 0) return;

        var regions = DetectorRegions.ForGame(game)!;
        var templates = DetectorTemplates.Load(game, regions, TemplateRoot);

        Assert.Empty(DetectorTemplates.Match(templates, ToFrame(Path.Combine(folder, file), regions)));
    }
}
