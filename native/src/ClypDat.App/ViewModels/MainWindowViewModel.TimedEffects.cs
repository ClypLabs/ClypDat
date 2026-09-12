using System.Collections.ObjectModel;
using ClypDat.Core.Settings;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task RenderEffectPreviewAsync(string output, CancellationToken token, double? frameTime = null)
    {
        var source = SelectedVideoPath;
        var sourceWidth = SelectedSourceWidth;
        var sourceHeight = SelectedSourceHeight;
        var crop = ActiveCropRect;
        var start = frameTime ?? 0;
        var duration = frameTime is null ? Duration.TotalSeconds : 1.0 / 30;
        var texts = TextEffects.ToArray();
        var blurs = BlurEffects.ToArray();
        var scale = Math.Min(1, Math.Min(1280.0 / sourceWidth, 720.0 / sourceHeight));
        var width = Math.Max(2, (int)(sourceWidth * scale) / 2 * 2);
        var height = Math.Max(2, (int)(sourceHeight * scale) / 2 * 2);
        var cropWidth = crop?.Width ?? sourceWidth;
        var cropHeight = crop?.Height ?? sourceHeight;
        var outputWidth = Math.Max(2, (int)(cropWidth * scale) / 2 * 2);
        var outputHeight = Math.Max(2, (int)(cropHeight * scale) / 2 * 2);
        var capturedSpec = CaptureOverlayBurnSpec() is { } captured
            ? captured with { TrimStartSeconds = start, TrimEndSeconds = start + duration, Speed = 1 } : null;
        var spotifySpec = CaptureSpotifyRenderSpec() is { } spotify
            ? spotify with { Start = start, Duration = duration, Speed = 1, Width = outputWidth, Height = outputHeight } : null;
        using var effects = await TimedEffectRender.PrepareAsync(texts, blurs, start, start + duration, 1, outputWidth, outputHeight, token);
        using var card = spotifySpec is null ? null : await SpotifyOverlayAnimation.PrepareAsync(spotifySpec, token);
        var layers = capturedSpec is null ? (Camera: (ClipOverlayBurnLayer?)null, Keyboard: (ClipOverlayBurnLayer?)null)
            : await ClipOverlayBurn.PrepareAsync(capturedSpec, outputWidth, outputHeight, token);
        using var render = new ClipOverlayRender { Camera = layers.Camera, Keyboard = layers.Keyboard, Spotify = card, Effects = effects };
        var args = new List<string> { "-y", "-ss", start.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), "-i", source };
        var composites = AppendOverlayInputs(args, render);
        var filter = ClipRenderFilters.BuildVideoFilter(crop, 1, $"scale={outputWidth}:{outputHeight},fps=30");
        var composed = ClipRenderFilters.ComposeWithOverlays(filter, composites, "[effectsource]", "[effectresult]");
        if (composites.Count == 0) composed = $"[effectsource]{filter}[effectresult]";
        string graph;
        if (crop is { } c)
            graph = $"[0:v:0]split[original][effectsource];[original]scale={width}:{height},fps=30[originalsmall];{composed};[originalsmall][effectresult]overlay={(int)(c.X * scale)}:{(int)(c.Y * scale)}[preview]";
        else graph = composed.Replace("[effectsource]", "[0:v:0]").Replace("[effectresult]", "[preview]");
        args.AddRange(new[] { "-filter_complex", graph, "-map", "[preview]", "-an" });
        if (frameTime is null) args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart" });
        else args.AddRange(new[] { "-frames:v", "1", "-c:v", "png", "-f", "image2" });
        await TimedEffectPreview.RenderAsync(args, output, token);
    }
    public ObservableCollection<TimedVideoEffect> TextEffects { get; } = [];
    public ObservableCollection<TimedVideoEffect> BlurEffects { get; } = [];
    public event EventHandler? TimedEffectsChanged;
    public bool DrawBlurRectangle { get; set; }
    private Guid? _selectedTimedEffectId;
    public Guid? SelectedTimedEffectId
    {
        get => _selectedTimedEffectId;
        set { if (_selectedTimedEffectId == value) return; _selectedTimedEffectId = value; TimedEffectsChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void NotifyTimedEffectsChanged()
    {
        OnPropertyChanged(nameof(EditorTimelineHeight));
        OnEditorEffectsChanged();
        TimedEffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimedVideoEffect AddTimedEffect(bool blur)
    {
        var list = blur ? BlurEffects : TextEffects;
        if (list.Count >= TimedEffectState.MaximumItems) throw new InvalidOperationException("Maximum 32 effects per lane.");
        var end = (TrimEnd > TrimStart ? TrimEnd : Duration).TotalSeconds;
        var start = Math.Clamp(CurrentTime.TotalSeconds, TrimStart.TotalSeconds, Math.Max(TrimStart.TotalSeconds, end - .01));
        var item = new TimedVideoEffect { Start = start, End = Math.Min(end, start + 3),
            X = blur ? .35 : .1, Y = blur ? .35 : .75, Width = blur ? .3 : .8, Height = blur ? .3 : .2 };
        TimedEffectState.Validate([item]);
        ValidateTimedEdit(list.Append(item), blur);
        list.Add(item);
        DrawBlurRectangle = blur;
        SelectedTimedEffectId = item.Id;
        NotifyTimedEffectsChanged();
        return item;
    }

    public void UpdateTimedEffect(TimedVideoEffect item, bool blur)
    {
        TimedEffectState.Validate([item]);
        var list = blur ? BlurEffects : TextEffects;
        var index = list.ToList().FindIndex(e => e.Id == item.Id);
        if (index < 0) return;
        ValidateTimedEdit(list.Select(e => e.Id == item.Id ? item : e), blur);
        list[index] = item;
        NotifyTimedEffectsChanged();
    }

    public void DuplicateTimedEffect(TimedVideoEffect item, bool blur)
    {
        var list = blur ? BlurEffects : TextEffects;
        var copy = item with { Id = Guid.NewGuid() };
        ValidateTimedEdit(list.Append(copy), blur);
        list.Add(copy); SelectedTimedEffectId = copy.Id; NotifyTimedEffectsChanged();
    }

    private void ValidateTimedEdit(IEnumerable<TimedVideoEffect> items, bool blur)
    {
        var edit = new ClipEditSettings { TextEffects = blur ? TextEffects.ToList() : items.ToList(),
            BlurEffects = blur ? items.ToList() : BlurEffects.ToList(), Description = EditorDescription ?? "" };
        if (ClipEditSidecar.SerializeValidated(edit).Length > 60 * 1024)
            throw new InvalidDataException("Clip edits are too large. Shorten captions or remove effects.");
    }
}
