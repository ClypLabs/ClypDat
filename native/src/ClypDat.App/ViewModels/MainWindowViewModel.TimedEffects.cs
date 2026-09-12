using System.Collections.ObjectModel;
using ClypDat.Core.Settings;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<TimedVideoEffect> TextEffects { get; } = [];
    public ObservableCollection<TimedVideoEffect> BlurEffects { get; } = [];
    /// <summary>Raised for every change, including unsaved mid-drag ones, so the
    /// overlay, the video track and the inspector redraw together.</summary>
    public event EventHandler? TimedEffectsChanged;
    /// <summary>Asks the view to move the playhead, e.g. to a clip picked from the list.</summary>
    public event EventHandler<TimeSpan>? TimedEffectSeekRequested;
    /// <summary>Asks the inspector to put the caret in the selected caption.</summary>
    public event EventHandler? TimedEffectCaptionFocusRequested;
    /// <summary>Asks the view to pause and add text (false) or blur (true) at
    /// the playhead. The view owns playback, so adding goes through it.</summary>
    public event EventHandler<bool>? TimedEffectAddRequested;
    private Guid? _selectedTimedEffectId;
    public Guid? SelectedTimedEffectId
    {
        get => _selectedTimedEffectId;
        set { if (_selectedTimedEffectId == value) return; _selectedTimedEffectId = value; TimedEffectsChanged?.Invoke(this, EventArgs.Empty); }
    }

    public TimedVideoEffect? SelectedTimedEffect => FindTimedEffect(SelectedTimedEffectId, out _);

    public TimedVideoEffect? FindTimedEffect(Guid? id, out bool blur)
    {
        blur = false;
        if (id is null) return null;
        if (TextEffects.FirstOrDefault(e => e.Id == id) is { } text) return text;
        blur = true;
        return BlurEffects.FirstOrDefault(e => e.Id == id);
    }

    public bool IsBlurEffect(Guid id) => BlurEffects.Any(e => e.Id == id);

    /// <summary>Selecting an effect releases the Spotify/camera selection, since
    /// only one layer owns the handles on the video at a time.</summary>
    public void SelectTimedEffect(Guid? id)
    {
        if (id is not null)
        {
            IsSpotifyOverlaySelected = false;
            DeselectCapturedOverlays();
            OpenEditorSidebar(EditorSidebarSection.Effects);
        }
        SelectedTimedEffectId = id;
    }

    public void RequestTimedEffectSeek(TimeSpan time) => TimedEffectSeekRequested?.Invoke(this, time);
    public void RequestAddTimedEffect(bool blur) => TimedEffectAddRequested?.Invoke(this, blur);
    public void RequestTimedEffectCaptionFocus() => TimedEffectCaptionFocusRequested?.Invoke(this, EventArgs.Empty);

    public void NotifyTimedEffectsChanged()
    {
        OnEditorEffectsChanged();
        TimedEffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimedVideoEffect AddTimedEffect(bool blur, double? atSeconds = null)
    {
        var list = blur ? BlurEffects : TextEffects;
        if (list.Count >= TimedEffectState.MaximumItems) throw new InvalidOperationException("Maximum 32 effects per type.");
        var duration = Duration.TotalSeconds;
        var rangeStart = TrimEnd > TrimStart ? TrimStart.TotalSeconds : 0;
        var rangeEnd = TrimEnd > TrimStart ? TrimEnd.TotalSeconds : duration;
        var start = Math.Clamp(atSeconds ?? CurrentTime.TotalSeconds, rangeStart, Math.Max(rangeStart, rangeEnd - .1));
        var end = Math.Min(rangeEnd, start + 3);
        if (end - start < .1) end = Math.Min(Math.Max(duration, start + .1), start + 3);
        var item = new TimedVideoEffect
        {
            Start = start, End = end,
            X = blur ? .35 : .1, Y = blur ? .35 : .4, Width = blur ? .3 : .8, Height = blur ? .3 : .2,
            Text = blur ? "" : "Your text", FontSize = 72, Bold = true, Outline = 3
        };
        TimedEffectState.Validate([item]);
        ValidateTimedEdit(list.Append(item), blur);
        list.Add(item);
        SelectTimedEffect(item.Id);
        NotifyTimedEffectsChanged();
        return item;
    }

    /// <summary>Replaces an effect by identity. persist=false is the mid-gesture
    /// path: it redraws without touching the sidecar, and the gesture calls
    /// <see cref="CommitTimedEffects"/> once on release.</summary>
    public void SetTimedEffect(TimedVideoEffect item, bool persist)
    {
        TimedEffectState.Validate([item]);
        var blur = IsBlurEffect(item.Id);
        var list = blur ? BlurEffects : TextEffects;
        var index = list.ToList().FindIndex(e => e.Id == item.Id);
        if (index < 0) return;
        if (list[index] == item && !persist) return;
        ValidateTimedEdit(list.Select(e => e.Id == item.Id ? item : e), blur);
        list[index] = item;
        if (persist) NotifyTimedEffectsChanged();
        else TimedEffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CommitTimedEffects() => NotifyTimedEffectsChanged();

    public void RemoveTimedEffect(Guid id)
    {
        var list = IsBlurEffect(id) ? BlurEffects : TextEffects;
        var index = list.ToList().FindIndex(e => e.Id == id);
        if (index < 0) return;
        list.RemoveAt(index);
        if (SelectedTimedEffectId == id) _selectedTimedEffectId = null;
        NotifyTimedEffectsChanged();
    }

    /// <summary>Copies an effect to just after the original, or on top of it
    /// when the clip has no room left after it.</summary>
    public TimedVideoEffect? DuplicateTimedEffect(Guid id)
    {
        if (FindTimedEffect(id, out var blur) is not { } item) return null;
        var list = blur ? BlurEffects : TextEffects;
        if (list.Count >= TimedEffectState.MaximumItems) throw new InvalidOperationException("Maximum 32 effects per type.");
        var length = item.End - item.Start;
        var copy = item.End + length <= Duration.TotalSeconds
            ? item with { Id = Guid.NewGuid(), Start = item.End, End = item.End + length }
            : item with { Id = Guid.NewGuid() };
        ValidateTimedEdit(list.Append(copy), blur);
        list.Add(copy);
        SelectTimedEffect(copy.Id);
        NotifyTimedEffectsChanged();
        return copy;
    }

    private void ValidateTimedEdit(IEnumerable<TimedVideoEffect> items, bool blur)
    {
        var edit = new ClipEditSettings { TextEffects = blur ? TextEffects.ToList() : items.ToList(),
            BlurEffects = blur ? items.ToList() : BlurEffects.ToList(), Description = EditorDescription ?? "" };
        if (ClipEditSidecar.SerializeValidated(edit).Length > 60 * 1024)
            throw new InvalidDataException("Clip edits are too large. Shorten captions or remove effects.");
    }
}
