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
    [property: JsonPropertyName("file")] string File,
    // Absent means Raw, so a template that has not had its threshold measured
    // against the other scoring keeps behaving exactly as it did.
    [property: JsonPropertyName("scoring")] string? Scoring = null,
    // [x, y] slack, frame-normalized, the banner may sit away from Region.
    // Absent means the banner is pinned to Region exactly.
    [property: JsonPropertyName("searchPad")] double[]? SearchPad = null);

public sealed record DetectorTemplateManifest(
    [property: JsonPropertyName("games")] Dictionary<string, DetectorTemplateEntry[]> Games);

public sealed record LoadedTemplate(
    string EventId,
    string Label,
    int Slot,
    NormalizedRegion SlotRegion,
    double Threshold,
    GrayTemplateMatcher Matcher,
    // Slot-relative area to slide a SlotRegion-sized window across, for a
    // banner that does not land on the same pixels every time.
    NormalizedRegion? SearchRegion = null);

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
                NormalizedRegion? search = null;
                if (entry.SearchPad is [var padX, var padY] && (padX > 0 || padY > 0))
                {
                    var left = Math.Max(slot.Value.X, frameRegion.X - padX);
                    var top = Math.Max(slot.Value.Y, frameRegion.Y - padY);
                    var right = Math.Min(slot.Value.X + slot.Value.Width, frameRegion.X + frameRegion.Width + padX);
                    var bottom = Math.Min(slot.Value.Y + slot.Value.Height, frameRegion.Y + frameRegion.Height + padY);
                    search = GrayTemplateMatcher.ToSlotRelative(new NormalizedRegion(left, top, right - left, bottom - top), slot.Value);
                }
                loaded.Add(new LoadedTemplate(
                    entry.EventId,
                    entry.Label,
                    entry.Slot,
                    GrayTemplateMatcher.ToSlotRelative(frameRegion, slot.Value),
                    entry.Threshold,
                    GrayTemplateMatcher.FromGray(template, ParseScoring(entry.Scoring)),
                    search));
            }
            return loaded;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            return Array.Empty<LoadedTemplate>();
        }
    }

    /// <summary>
    /// An unknown name falls back to Raw rather than throwing: a manifest naming
    /// a scoring this build does not have should degrade to the old behaviour,
    /// not take the whole pack down with it.
    /// </summary>
    private static TemplateScoring ParseScoring(string? scoring) =>
        string.Equals(scoring, "highpass3", StringComparison.OrdinalIgnoreCase)
            ? TemplateScoring.HighPass3
            : TemplateScoring.Raw;

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

    /// <summary>How strongly one template's banner is on screen in this frame.</summary>
    public static double Score(LoadedTemplate template, DetectorFrameSnapshot frame)
    {
        var slot = SlotImage(frame, template.Slot);
        if (template.SearchRegion is { } search)
        {
            // The window keeps the template's own size; only its position is
            // searched, so a few pixels of drift cannot sink the score.
            var window = template.SlotRegion.ToPixelRect(slot.Width, slot.Height);
            return template.Matcher.ScoreSearch(GrayTemplateMatcher.Crop(slot, search), window.Width, window.Height);
        }
        // ScoreBest, not Score: the region is a search band because these
        // banners shift vertically as the game stacks lines above them.
        return template.Matcher.ScoreBest(GrayTemplateMatcher.Crop(slot, template.SlotRegion));
    }

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
            var score = Score(template, frame);
            if (score >= template.Threshold) hits.Add((template, score));
        }
        hits.Sort((left, right) => right.Score.CompareTo(left.Score));
        return hits;
    }
}
