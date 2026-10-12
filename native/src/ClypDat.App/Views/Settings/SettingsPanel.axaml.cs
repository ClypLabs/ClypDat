using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.Converters;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class SettingsPanel : UserControl
{
    // Keep the same order used by search results before sections were lazy.
    private static readonly (string Name, Func<Control> Create)[] Sections =
    [
        ("Replay Buffer", () => new ReplayBufferSection()),
        ("Video Overlays", () => new VideoOverlaysSection()),
        ("Overlays and Notifications", () => new OverlaysSection()),
        ("Appearance", () => new ThemesSection()),
        ("Discord Rich Presence", () => new DiscordRichPresenceSection()),
        ("General", () => new GeneralSection()),
        ("Account", () => new IntegrationsSection()),
        ("Game Detection", () => new GameDetectionSection()),
        ("Import Clips", () => new ImportClipsSection()),
        ("Auto-Clip", () => new AutoClipSection()),
        ("Game Stats", () => new GameStatsSection()),
        ("Audio", () => new AudioSection()),
        ("Advanced", () => new AdvancedSection()),
        ("Custom Game Settings", () => new CustomGameSection()),
        ("Game Audio Exclusions", () => new GameAudioExclusionsSection()),
        ("About", () => new AboutSection())
    ];
    private readonly Dictionary<string, Control> _activeSections = new(StringComparer.Ordinal);
    private MainWindowViewModel? _observedModel;
    private bool _attached;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ObserveModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopObservingModel();
        UpdateSections(false, string.Empty, string.Empty);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached) ObserveModel();
    }

    private void ObserveModel()
    {
        var model = DataContext as MainWindowViewModel;
        if (!ReferenceEquals(model, _observedModel)) UpdateSections(false, string.Empty, string.Empty);
        StopObservingModel();
        _observedModel = model;
        if (_observedModel is not null) _observedModel.PropertyChanged += ModelChanged;
        RefreshSections();
    }

    private void StopObservingModel()
    {
        if (_observedModel is not null) _observedModel.PropertyChanged -= ModelChanged;
        _observedModel = null;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsVisible) or nameof(MainWindowViewModel.SelectedSettingsSection)
            or nameof(MainWindowViewModel.SettingsSearchText)) RefreshSections();
    }

    private void RefreshSections() => UpdateSections(_observedModel?.IsSettingsVisible == true,
        _observedModel?.SelectedSettingsSection ?? string.Empty, _observedModel?.SettingsSearchText ?? string.Empty);

    internal void UpdateSections(bool settingsVisible, string selectedSection, string searchText)
    {
        var searching = !string.IsNullOrWhiteSpace(searchText);
        var required = settingsVisible
            ? Sections.Where(section => searching
                ? SettingsSearchMatchConverter.MatchesSection(searchText, section.Name)
                : section.Name == selectedSection).ToArray()
            : [];
        var names = required.Select(section => section.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _activeSections.Keys.Where(name => !names.Contains(name)).ToArray())
        {
            // Removal detaches lifetime-managed controls (overlay preview,
            // About artwork) and their view-model/resource subscriptions.
            SettingsSections.Children.Remove(_activeSections[stale]);
            _activeSections.Remove(stale);
        }
        for (var index = 0; index < required.Length; ++index)
        {
            var section = required[index];
            if (_activeSections.ContainsKey(section.Name)) continue;
            var view = section.Create();
            SettingsSections.Children.Insert(index, view);
            _activeSections.Add(section.Name, view);
        }
        // A matching section stays mounted through query changes, preserving
        // focused editors. MainWindow still owns the search and scroll hosts.
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void ClearSettingsSearchButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ClearSettingsSearchButton_OnClick(sender, e);

    private void SettingsNavButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.SettingsNavButton_OnClick(sender, e);

    private void FixCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.FixCustomGameQualityWarningButton_OnClick(sender, e);

    private void AcknowledgeCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AcknowledgeCustomGameQualityWarningButton_OnClick(sender, e);

    private void HideCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HideCustomGameQualityWarningButton_OnClick(sender, e);
    internal TextBox SearchBox => SettingsSearchBox;

    internal ScrollViewer ScrollViewer => SettingsScrollViewer;
}
