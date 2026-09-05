using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

/// <summary>
/// One banner the detector recognises by appearance rather than by reading it.
/// <see cref="Region"/> is measured against the FULL frame, the same coordinate
/// system as <see cref="DetectorRegions"/>, and converted to a slot-relative
/// crop at load. <see cref="Slot"/> is which of the three frame crops it lives
/// in.
/// </summary>
public sealed record DetectorTemplateEntry(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("region")] double[] Region,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("file")] string File);

public sealed record DetectorTemplateManifest(
    [property: JsonPropertyName("games")] Dictionary<string, DetectorTemplateEntry[]> Games);

public sealed record LoadedTemplate(
    string EventId,
    string Label,
    int Slot,
    NormalizedRegion SlotRegion,
    double Threshold,
    GrayTemplateMatcher Matcher);

/// <summary>
/// Loads the banner templates shipped beside the app and scores a captured
/// frame against them.
/// </summary>
public static class DetectorTemplates
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "detector-templates");

    /// <summary>
    /// Returns an empty list rather than throwing when the templates are
    /// missing: the detector still runs its OCR events, and the status line is
    /// where a missing pack should be reported.
    /// </summary>
    public static IReadOnlyList<LoadedTemplate> Load(string gameId, DetectorRegionSet regions, string? root = null)
    {
        try
        {
            var folder = root ?? DefaultRoot;
            var manifestPath = Path.Combine(folder, "templates.json");
            if (!System.IO.File.Exists(manifestPath)) return Array.Empty<LoadedTemplate>();
            var manifest = JsonSerializer.Deserialize<DetectorTemplateManifest>(System.IO.File.ReadAllBytes(manifestPath), JsonOptions);
            if (manifest?.Games is null || !manifest.Games.TryGetValue(gameId, out var entries)) return Array.Empty<LoadedTemplate>();

            var loaded = new List<LoadedTemplate>(entries.Length);
            foreach (var entry in entries)
            {
                if (entry.Region is not { Length: 4 }) continue;
                var imagePath = Path.Combine(folder, entry.File);
                if (!System.IO.File.Exists(imagePath)) continue;
                var slot = SlotRegion(regions, entry.Slot);
                if (slot is null) continue;
                var frameRegion = new NormalizedRegion(entry.Region[0], entry.Region[1], entry.Region[2], entry.Region[3]);
                var template = GrayPng.Read(imagePath);
                loaded.Add(new LoadedTemplate(
                    entry.EventId,
                    entry.Label,
                    entry.Slot,
                    GrayTemplateMatcher.ToSlotRelative(frameRegion, slot.Value),
                    entry.Threshold,
                    GrayTemplateMatcher.FromGray(template)));
            }
            return loaded;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            return Array.Empty<LoadedTemplate>();
        }
    }

    private static NormalizedRegion? SlotRegion(DetectorRegionSet regions, int slot) => slot switch
    {
        0 => regions.First,
        1 => regions.Second,
        2 => regions.Third,
        _ => null
    };

    private static GrayDetectorImage SlotImage(DetectorFrameSnapshot frame, int slot) => slot switch
    {
        0 => frame.First,
        1 => frame.Second,
        _ => frame.Third
    };

    /// <summary>
    /// Every template whose banner is on screen this frame, best first, so a
    /// caller that only wants one takes the strongest match.
    /// </summary>
    public static IReadOnlyList<(LoadedTemplate Template, double Score)> Match(
        IReadOnlyList<LoadedTemplate> templates, DetectorFrameSnapshot frame)
    {
        if (templates.Count == 0) return Array.Empty<(LoadedTemplate, double)>();
        var hits = new List<(LoadedTemplate Template, double Score)>();
        foreach (var template in templates)
        {
            var crop = GrayTemplateMatcher.Crop(SlotImage(frame, template.Slot), template.SlotRegion);
            // ScoreBest, not Score: the region is a search band because these
            // banners shift vertically as the game stacks lines above them.
            var score = template.Matcher.ScoreBest(crop);
            if (score >= template.Threshold) hits.Add((template, score));
        }
        hits.Sort((left, right) => right.Score.CompareTo(left.Score));
        return hits;
    }
}
