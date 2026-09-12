using ClypDat.Core.Settings;
using Avalonia.Media;

namespace ClypDat.App.Services;

public static class TimedEffectState
{
    public const int MaximumItems = 32;
    public const int MaximumCaptionLength = 2000;
    public static readonly string[] BlurShapes = ["Rectangle", "Rounded", "Ellipse"];

    /// <summary>Assigns each effect the first row free at its start, so overlapping
    /// clips stack inside the video track instead of hiding each other.</summary>
    public static (Dictionary<Guid, int> Rows, int Count) PackRows(IEnumerable<TimedVideoEffect> effects)
    {
        var ends = new List<double>();
        var rows = new Dictionary<Guid, int>();
        foreach (var effect in effects.OrderBy(e => e.Start).ThenBy(e => e.Id))
        {
            var row = ends.FindIndex(end => end <= effect.Start);
            if (row < 0) { row = ends.Count; ends.Add(effect.End); } else ends[row] = effect.End;
            rows[effect.Id] = row;
        }
        return (rows, Math.Max(1, ends.Count));
    }

    public static void Validate(IEnumerable<TimedVideoEffect> effects)
    {
        var items = effects.ToArray();
        if (items.Length > MaximumItems || items.Select(e => e.Id).Distinct().Count() != items.Length)
            throw new InvalidDataException("Use at most 32 effects with unique identities.");
        foreach (var e in items)
        {
            if (new[] { e.Start, e.End, e.X, e.Y, e.Width, e.Height, e.FontSize, e.Outline, e.BackgroundOpacity, e.Strength }.Any(v => !double.IsFinite(v)) ||
                e.Start < 0 || e.End <= e.Start || e.X < 0 || e.Y < 0 || e.Width <= 0 || e.Height <= 0 || e.X + e.Width > 1.000001 || e.Y + e.Height > 1.000001 ||
                e.FontSize < 8 || e.FontSize > 300 || e.Outline < 0 || e.Outline > 12 || e.BackgroundOpacity < 0 || e.BackgroundOpacity > 1 || e.Strength < 1 || e.Strength > 100)
                throw new InvalidDataException("Effect geometry, timing or strength is invalid.");
            if (e.Text is null || e.Text.Length > MaximumCaptionLength || string.IsNullOrWhiteSpace(e.Font) || e.Font.Length > 200 ||
                !Color.TryParse(e.Colour, out _) || !Color.TryParse(e.Background, out _) || e.Alignment is not ("Left" or "Center" or "Right"))
                throw new InvalidDataException("Check caption length (maximum 2000), font, colours and alignment.");
            if (!BlurShapes.Contains(e.Shape))
                throw new InvalidDataException("Blur shape must be Rectangle, Rounded or Ellipse.");
        }
    }

    public static List<TimedVideoEffect> Rebase(IEnumerable<TimedVideoEffect> effects, double start, double end, double speed)
    {
        Validate(effects);
        speed = ClipRenderFilters.NormalizeSpeed(speed);
        return effects.Where(e => e.End > start && e.Start < end)
            .Select(e => e with { Start = (Math.Max(start, e.Start) - start) / speed, End = (Math.Min(end, e.End) - start) / speed }).ToList();
    }

    public static SpotifyOverlayBounds Pixels(TimedVideoEffect e, int width, int height)
    {
        var x = Math.Clamp((int)Math.Round(e.X * width), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(e.Y * height), 0, height - 1);
        return new(x, y, Math.Clamp((int)Math.Round(e.Width * width), 1, width - x), Math.Clamp((int)Math.Round(e.Height * height), 1, height - y));
    }

    public static TimedVideoEffect Reproject(TimedVideoEffect e, ClipRenderFilters.CropRect oldCrop, ClipRenderFilters.CropRect newCrop)
    {
        var width = Math.Clamp(e.Width * oldCrop.Width / newCrop.Width, .01, 1);
        var height = Math.Clamp(e.Height * oldCrop.Height / newCrop.Height, .01, 1);
        return e with { Width = width, Height = height,
            X = Math.Clamp((oldCrop.X + e.X * oldCrop.Width - newCrop.X) / newCrop.Width, 0, 1 - width),
            Y = Math.Clamp((oldCrop.Y + e.Y * oldCrop.Height - newCrop.Y) / newCrop.Height, 0, 1 - height) };
    }
}
