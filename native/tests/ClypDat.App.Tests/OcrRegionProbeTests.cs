using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// Diagnostic: runs the real Windows OCR over real crops so region and legibility
// problems surface offline instead of as "the detector just never fires".
public sealed class OcrRegionProbeTests
{
    [Fact]
    public async Task ProbePreprocessedCrops()
    {
        var folder = Environment.GetEnvironmentVariable("CLYPDAT_OCR_PROBE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        var reader = new WindowsOcrFrameReader();
        var report = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.png").OrderBy(item => item))
        {
            var words = await reader.ReadAsync(file, null);
            report.Add($"{Path.GetFileName(file),-32} [{string.Join(' ', words.Select(word => word.Text))}]");
        }

        await File.WriteAllLinesAsync(Path.Combine(Path.GetTempPath(), "clypdat-ocr-probe.txt"), report);
        Assert.NotEmpty(report);
    }

    // HELLDIVERS is the one game still relying purely on OCR, so its phrases
    // need to actually come back readable.
    [Fact]
    public async Task ProbeHelldiversRegions()
    {
        var folder = Environment.GetEnvironmentVariable("CLYPDAT_HD_PROBE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        var regions = DetectorRegions.ForGame("helldivers2")!;
        var reader = new WindowsOcrFrameReader();
        var report = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.png").OrderBy(item => item))
        {
            foreach (var (name, region) in new[]
                     {
                         ("centreBanner", regions.First),
                         ("missionPanel", regions.Second),
                         ("killCounter", regions.Third)
                     })
            {
                var words = await reader.ReadAsync(file, region);
                report.Add($"{Path.GetFileName(file),-32} {name,-13} [{string.Join(' ', words.Select(word => word.Text))}]");
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(Path.GetTempPath(), "clypdat-hd-probe.txt"), report);
        Assert.NotEmpty(report);
    }
}
