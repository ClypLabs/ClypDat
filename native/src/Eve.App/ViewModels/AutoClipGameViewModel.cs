using System.Collections.ObjectModel;
using Eve.App.Services;
using Eve.Core.Settings;

namespace Eve.App.ViewModels;

public sealed class AutoClipGameViewModel : ViewModelBase
{
    private readonly AutoClipGameSettings _settings;
    private readonly Action _save;
    private bool _isExpanded;
    private bool _isSearchMatch = true;
    private string _statusText = "Waiting for game";

    public AutoClipGameViewModel(AutoClipGameDefinition definition, AutoClipGameSettings settings, Action save)
    {
        Definition = definition;
        _settings = settings;
        _save = save;
        Groups = new ObservableCollection<AutoClipGroupViewModel>(definition.Groups.Select(group => new AutoClipGroupViewModel(group, definition.Events.Where(item => item.GroupId == group.Id), _settings, SaveAndRefresh)));
        UngroupedEvents = new ObservableCollection<AutoClipEventViewModel>(definition.Events.Where(item => item.GroupId is null).Select(item => new AutoClipEventViewModel(item, _settings, SaveAndRefresh)));
    }

    public AutoClipGameDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public ObservableCollection<AutoClipGroupViewModel> Groups { get; }
    public ObservableCollection<AutoClipEventViewModel> UngroupedEvents { get; }
    public bool IsSetupRequired => Definition.RequiresSetup;
    public bool IsEnabled { get => _settings.Enabled; set { if (_settings.Enabled == value) return; _settings.Enabled = value; SaveAndRefresh(); } }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsSearchMatch { get => _isSearchMatch; set => SetProperty(ref _isSearchMatch, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string EventsSummary
    {
        get
        {
            var selected = Definition.Events.Count(item => _settings.Events.TryGetValue(item.Id, out var enabled) && enabled);
            return selected == 0 ? "No events selected" : selected == Definition.Events.Count ? "All events selected" : $"{selected} of {Definition.Events.Count} events selected";
        }
    }

    public void Refresh() { OnPropertyChanged(nameof(EventsSummary)); foreach (var group in Groups) group.Refresh(); }
    private void SaveAndRefresh() { Refresh(); _save(); }
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
    public bool? IsChecked
    {
        get { var count = _definitions.Count(item => _settings.Events.TryGetValue(item.Id, out var enabled) && enabled); return count == 0 ? false : count == _definitions.Count ? true : null; }
        set { var enabled = value == true; foreach (var item in _definitions) _settings.Events[item.Id] = enabled; Refresh(); _changed(); }
    }
    public void Refresh() { OnPropertyChanged(nameof(IsChecked)); foreach (var item in Events) item.Refresh(); }
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
