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

    /// <summary>
    /// Frames committed to the repo, so these tests RUN. They used to read a
    /// folder named by CLYPDAT_FRAME_PROBE_DIR and return quietly when it was
    /// unset, which meant they never ran anywhere - and that is how a quintuple
    /// template that outscored every other banner on empty scenery shipped and
    /// filled a tester's library with clips of nothing.
    ///
    /// Cut from that tester's own clips at 1920x1080 greyscale, the plane the
    /// detector actually reads.
    /// </summary>
    private static string FixtureRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "detector")))
                directory = directory.Parent;
            return directory is null ? string.Empty : Path.Combine(directory.FullName, "tests", "fixtures", "detector");
        }
    }

    // Known-answer frames drive the whole pipeline: crop the three slots the way
    // the live detector does, match, and check the right banner wins. These are
    // the numbers that justify the thresholds in templates.json.
    //
    // No quintuple-kill or team-kill case: there is no real one to point at. Every
    // clip the tester's app labelled "Quintuple Kill" turned out to contain no
    // banner at all. Add them when a genuine frame exists, not before.
    [Theory]
    // Frames from a single real ladder - DOUBLE KILL, then TRIPLE, then QUADRUPLE -
    // none of them the frame their template was cut from. A self-match proves
    // nothing, and using one is how a broken double-kill template scored 1.000
    // while never firing in game.
    [InlineData("ow-double.png", "double-kill")]
    [InlineData("ow-triple.png", "triple-kill")]
    [InlineData("ow-quadruple.png", "quadruple-kill")]
    // The highlight bar, in both the spellings Overwatch uses for it. It is
    // matched rather than read because OCR returns "PIWOfWfCßMf" for it.
    [InlineData("ow-potg-game.png", "play-of-the-game")]
    [InlineData("ow-potg-match.png", "play-of-the-game")]
    public void TheRightBannerWinsOnAKnownFrame(string file, string expected)
    {
        var path = Path.Combine(FixtureRoot, file);
        Assert.True(File.Exists(path), $"Missing detector fixture: {path}");

        var regions = DetectorRegions.ForGame("overwatch")!;
        var templates = DetectorTemplates.Load("overwatch", regions, TemplateRoot);
        Assert.NotEmpty(templates);
        var hits = DetectorTemplates.Match(templates, ToFrame(path, regions));
        Assert.NotEmpty(hits);

        // Best per slot, the way LiveOverwatchDetector reads it. A highlight
        // frame legitimately carries two: the bar in the left column, and the
        // spectated player's streak in the kill feed.
        var winners = hits.GroupBy(hit => hit.Template.Slot).Select(group => group.First()).ToArray();
        var winner = Assert.Single(winners, hit => hit.Template.EventId == expected);

        // Nothing else in that slot may be close enough to be mistaken for it.
        Assert.All(hits.Where(hit => hit.Template.Slot == winner.Template.Slot
                                     && hit.Template.EventId != expected),
            hit => Assert.True(hit.Score < winner.Score - 0.1,
                $"{hit.Template.EventId} scored {hit.Score:F3} against {expected} at {winner.Score:F3}."));
    }

    // The regression guard for the false-positive flood: banner-free frames must
    // match nothing. Two are from a clip the app saved as "Quintuple Kill" that
    // contains no banner in any of its 31 frames; the third is from one where the
    // player was killed. Under the old raw correlation the quintuple template
    // scored up to 0.781 on frames like these, over its 0.70 threshold.
    [Theory]
    [InlineData("ow-empty-1.png")]
    [InlineData("ow-empty-2.png")]
    [InlineData("ow-empty-3.png")]
    public void AFrameWithNoBannerMatchesNothing(string file)
    {
        var path = Path.Combine(FixtureRoot, file);
        Assert.True(File.Exists(path), $"Missing detector fixture: {path}");

        var regions = DetectorRegions.ForGame("overwatch")!;
        var templates = DetectorTemplates.Load("overwatch", regions, TemplateRoot);
        Assert.NotEmpty(templates);

        var hits = DetectorTemplates.Match(templates, ToFrame(path, regions));
        Assert.Empty(hits);
    }
}
