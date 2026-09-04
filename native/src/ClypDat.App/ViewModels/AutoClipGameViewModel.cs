using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class AutoClipGameViewModel : ViewModelBase
{
    private readonly AutoClipGameSettings _settings;
    private readonly Action _save;
    private bool _isSearchMatch = true;
    private bool _isExpanded;
    private string _searchQuery = string.Empty;
    private string _statusText = "Waiting for game";

    public event EventHandler? SettingsChanged;

    public AutoClipGameViewModel(AutoClipGameDefinition definition, AutoClipGameSettings settings, Action save)
    {
        Definition = definition;
        _settings = settings;
        _save = save;
        if (definition.UsesDetectorPack) _statusText = "Not Installed";
        if (!string.IsNullOrWhiteSpace(definition.CoverAssetPath))
            CoverImage = new Bitmap(AssetLoader.Open(new Uri(definition.CoverAssetPath)));
        if (!string.IsNullOrWhiteSpace(definition.PortraitDetectionKey)) _ = LoadPortraitAsync();
        Groups = new ObservableCollection<AutoClipGroupViewModel>(definition.Groups.Select(group => new AutoClipGroupViewModel(group, definition.Events.Where(item => item.GroupId == group.Id), _settings, SaveAndRefresh)));
        UngroupedEvents = new ObservableCollection<AutoClipEventViewModel>(definition.Events.Where(item => item.GroupId is null).Select(item => new AutoClipEventViewModel(item, _settings, SaveAndRefresh)));
    }

    public AutoClipGameDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    private Bitmap? _coverImage;
    public Bitmap? CoverImage
    {
        get => _coverImage;
        private set => SetProperty(ref _coverImage, value);
    }
    public ObservableCollection<AutoClipGroupViewModel> Groups { get; }
    public ObservableCollection<AutoClipEventViewModel> UngroupedEvents { get; }
    public bool IsSetupRequired => Definition.RequiresSetup;
    public bool UsesDetectorPack => Definition.UsesDetectorPack;
    public bool IsCs2 => string.Equals(Id, "cs2", StringComparison.OrdinalIgnoreCase);
    public bool IsEnabled { get => _settings.Enabled; set { if (_settings.Enabled == value) return; _settings.Enabled = value; OnPropertyChanged(); SaveAndRefresh(); } }
    public bool DeathmatchClipping { get => _settings.DeathmatchClipping; set { if (_settings.DeathmatchClipping == value) return; _settings.DeathmatchClipping = value; OnPropertyChanged(); SaveAndRefresh(); } }
    public bool IsSearchMatch { get => _isSearchMatch; set => SetProperty(ref _isSearchMatch, value); }

    // Every game used to render every one of its events at once, which for
    // League is 18 checkboxes across three groups - the section ran several
    // screens and finding one event meant scrolling past all the others.
    // Collapsed, the whole section is one row per game.
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    // Opening a game opens its groups with it, so events are one click away
    // rather than two. Collapsing a group afterwards is what keeps a game the
    // size of League manageable.
    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var group in Groups) group.IsExpanded = expanded;
    }

    public void ToggleExpanded() => SetExpanded(!IsExpanded);

    // What the collapsed row says instead of showing the checkboxes.
    public int EnabledEventCount => Definition.Events.Count(item => _settings.Events.TryGetValue(item.Id, out var enabled) && enabled);
    public string SummaryLabel => EnabledEventCount == 1 ? "1 event" : $"{EnabledEventCount} events";

    // A query that matches something buried inside a collapsed group would
    // otherwise highlight a row with nothing visible in it.
    public void ApplySearchExpansion(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SetExpanded(false);
            return;
        }

        var nameMatches = Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var expandedAny = false;
        foreach (var group in Groups)
        {
            var hit = nameMatches || group.MatchesSearch(query);
            group.IsExpanded = hit;
            expandedAny |= hit;
        }

        IsExpanded = expandedAny || nameMatches || MatchesSearch(query);
    }
    // Bound to this row's own Name TextBlock via SettingsHighlight.Query -
    // that attached property needs the query on the SAME DataContext as the
    // TextBlock it's attached to (a per-game row here), not
    // MainWindowViewModel.SettingsSearchText directly, which lives one
    // level up and isn't reachable from this DataTemplate without an
    // ancestor-relative binding.
    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool MatchesSearch(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Definition.Events.Any(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        || (IsCs2 && ("Competitive".Contains(query, StringComparison.OrdinalIgnoreCase) || "Deathmatch Clipping".Contains(query, StringComparison.OrdinalIgnoreCase)));
    public void Refresh()
    {
        foreach (var group in Groups) group.Refresh();
        foreach (var item in UngroupedEvents) item.Refresh();
        OnPropertyChanged(nameof(EnabledEventCount));
        OnPropertyChanged(nameof(SummaryLabel));
    }
    private void SaveAndRefresh() { Refresh(); _save(); SettingsChanged?.Invoke(this, EventArgs.Empty); }

    private async Task LoadPortraitAsync()
    {
        var displayName = Definition.PortraitDisplayName ?? Name;
        try
        {
            var portrait = await Task.Run(() => GamePortraitService.TryLoad(displayName)).ConfigureAwait(false);
            if (portrait is null)
            {
                await GamePortraitService.EnsureCachedAsync(Definition.PortraitDetectionKey!, displayName).ConfigureAwait(false);
                portrait = await Task.Run(() => GamePortraitService.TryLoad(displayName)).ConfigureAwait(false);
            }
            if (portrait is not null) await Dispatcher.UIThread.InvokeAsync(() => CoverImage = portrait);
        }
        catch (Exception error)
        {
            AppLog.Error($"Auto-clip portrait load failed for '{Name}'", error);
        }
    }
}

public sealed class AutoClipGroupViewModel : ViewModelBase
{
    private readonly IReadOnlyList<AutoClipEventDefinition> _definitions;
    private readonly AutoClipGameSettings _settings;
    private readonly Action _changed;
    public AutoClipGroupViewModel(AutoClipGroupDefinition group, IEnumerable<AutoClipEventDefinition> definitions, AutoClipGameSettings settings, Action changed)
    {
        Name = group.Name; _definitions = definitions.ToArray(); _settings = settings; _changed = changed;
        Events = new ObservableCollection<AutoClipEventViewModel>(_definitions.Select(item => new AutoClipEventViewModel(item, settings, changed)));
    }
    public string Name { get; }
    public ObservableCollection<AutoClipEventViewModel> Events { get; }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public void ToggleExpanded() => IsExpanded = !IsExpanded;
    public int EnabledCount => _definitions.Count(item => _settings.Events.TryGetValue(item.Id, out var enabled) && enabled);
    public string SummaryLabel => $"{EnabledCount} of {_definitions.Count}";
    public bool MatchesSearch(string query) => _definitions.Any(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    public bool? IsChecked
    {
        get { var count = _definitions.Count(item => _settings.Events.TryGetValue(item.Id, out var enabled) && enabled); return count == 0 ? false : count == _definitions.Count ? true : null; }
        set { var enabled = value == true; foreach (var item in _definitions) _settings.Events[item.Id] = enabled; Refresh(); _changed(); }
    }
    public bool IsAllEnabled => IsChecked == true;
    public bool IsIndeterminate => IsChecked is null;
    public void Toggle() => IsChecked = IsChecked != true;
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsAllEnabled));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(SummaryLabel));
        foreach (var item in Events) item.Refresh();
    }
}

public sealed class AutoClipEventViewModel : ViewModelBase
{
    private readonly AutoClipEventDefinition _definition;
    private readonly AutoClipGameSettings _settings;
    private readonly Action _changed;
    public AutoClipEventViewModel(AutoClipEventDefinition definition, AutoClipGameSettings settings, Action changed) { _definition = definition; _settings = settings; _changed = changed; }
    public string Name => _definition.Name;
    public bool IsEnabled { get => _settings.Events.TryGetValue(_definition.Id, out var enabled) && enabled; set { if (IsEnabled == value) return; _settings.Events[_definition.Id] = value; OnPropertyChanged(); _changed(); } }
    public void Refresh() => OnPropertyChanged(nameof(IsEnabled));
}
