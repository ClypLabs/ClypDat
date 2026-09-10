using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Views.Settings;

public sealed partial class VideoOverlaysSection : UserControl
{
    public static readonly StyledProperty<CustomGameTabViewModel?> GameTabProperty =
        AvaloniaProperty.Register<VideoOverlaysSection, CustomGameTabViewModel?>(nameof(GameTab));
    public CustomGameTabViewModel? GameTab { get => GetValue(GameTabProperty); set => SetValue(GameTabProperty, value); }
    public bool IsGameScoped { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GameTabProperty)
        {
            if (change.OldValue is CustomGameTabViewModel oldTab) oldTab.PropertyChanged -= GameTabChanged;
            if (change.NewValue is CustomGameTabViewModel newTab) newTab.PropertyChanged += GameTabChanged;
            if (_owner is not null) CreateModel();
        }
    }
    private void GameTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomGameTabViewModel.HasOverlays)) CreateModel();
    }
    private MainWindowViewModel? _owner;
    private string? _dragLayer;
    private VideoOverlayManipulationMode _dragMode;
    private Point _lastPointer;

    public VideoOverlaysSection()
    {
        InitializeComponent();
        AttachedToVisualTree += Attached; DetachedFromVisualTree += Detached;
        SizeChanged += (_, _) => UpdateNarrowLayout();
        // Physical input still arrives through the shared monitor. Keep Space,
        // Enter and Tab from activating controls while building a key set.
        AddHandler(KeyDownEvent, (_, e) => { if (Model?.IsListening == true) e.Handled = true; }, RoutingStrategies.Tunnel);
    }

    private void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _owner = (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as MainWindowViewModel;
        if (_owner is null) return;
        CreateModel();
        _owner.PropertyChanged += OwnerChanged;
        UpdateVisibility();
        UpdateNarrowLayout();
    }

    private void CreateModel()
    {
        EndDrag();
        (DataContext as VideoOverlayViewModel)?.Dispose();
        if (_owner is null || (IsGameScoped && GameTab?.HasOverlays != true)) { DataContext = null; return; }
        var settings = IsGameScoped
            ? GameTab!.Profile.VideoOverlays ??= _owner.Settings.VideoOverlays.Copy()
            : _owner.Settings.VideoOverlays;
        DataContext = new VideoOverlayViewModel(settings, SaveOverlaySettings,
            () => _ = (TopLevel.GetTopLevel(this) as MainWindow)?.UpdateVideoOverlaySettingsAsync(),
            _owner.Settings.CustomKeyboardLayouts, _owner.Settings.VideoOverlays);
        UpdateVisibility();
        UpdateNarrowLayout();
    }

    private void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        EndDrag();
        (DataContext as IDisposable)?.Dispose();
        DataContext = null;
        if (_owner is not null) _owner.PropertyChanged -= OwnerChanged;
        _owner = null;
    }

    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedSettingsSection)) { CreateModel(); return; }
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedSettingsSection) or nameof(MainWindowViewModel.SettingsSearchText)
            or nameof(MainWindowViewModel.IsSettingsVisible)) UpdateVisibility();
    }

    private void SaveOverlaySettings()
    {
        if (_owner is null) return;
        foreach (var settings in _owner.Settings.CustomGameSettings.Values.Select(profile => profile.VideoOverlays)
                     .Append(_owner.Settings.VideoOverlays).OfType<VideoOverlaySettings>())
        {
            if (CustomKeyboardLibrary.IsCustomSelection(settings.KeyboardLayout) &&
                CustomKeyboardLibrary.Find(_owner.Settings.CustomKeyboardLayouts, settings.KeyboardLayout) is null)
            {
                settings.KeyboardLayout = "None";
                settings.KeyboardAnchor = null;
            }
        }
        _owner.SaveSettings();
    }

    // This section stays attached to the visual tree while hidden, so Detached
    // is not the signal for "the user left" - visibility is. The input hook
    // must not outlive the preview that needs it.
    private void UpdateVisibility()
    {
        IsVisible = IsGameScoped ? GameTab?.HasOverlays == true :
            _owner?.SelectedSettingsSection == "Video Overlays" || !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText);
        if (DataContext is not VideoOverlayViewModel model) return;
        var sectionVisible = !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText) ||
            _owner?.SelectedSettingsSection == (IsGameScoped ? "Custom Game Settings" : "Video Overlays");
        if (IsVisible && sectionVisible && _owner?.IsSettingsVisible == true) model.StartInputPreview(); else model.ClosePreview();
    }

    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();

    private VideoOverlayViewModel? Model => DataContext as VideoOverlayViewModel;
    private static T? TagOf<T>(object? sender) where T : class => (sender as Control)?.Tag as T;

    private void NewKeySet_OnClick(object? sender, RoutedEventArgs e) => Model?.NewLayout();
    private void EditKeySet_OnClick(object? sender, RoutedEventArgs e)
    { if (TagOf<CustomKeyboardLayout>(sender) is { } layout) Model?.EditLayout(layout); }
    private void DuplicateKeySet_OnClick(object? sender, RoutedEventArgs e)
    { if (TagOf<CustomKeyboardLayout>(sender) is { } layout) Model?.DuplicateLayout(layout); }
    private void DeleteKeySet_OnClick(object? sender, RoutedEventArgs e)
    { if (TagOf<CustomKeyboardLayout>(sender) is { } layout) Model?.DeleteLayout(layout); }
    private void RemoveKeySetKey_OnClick(object? sender, RoutedEventArgs e)
    { if (TagOf<CustomKeyCap>(sender) is { } cap) Model?.RemoveDraftKey(cap); }
    private void ListenKeys_OnClick(object? sender, RoutedEventArgs e) => Model?.ToggleListening();
    private void CloseKeySet_OnClick(object? sender, RoutedEventArgs e) => Model?.CloseLayoutEditor();
    private void ApplyKeySet_OnClick(object? sender, RoutedEventArgs e) => Model?.ApplyLayout();

    // A window is the layer, so pressing one both selects it and starts the
    // drag. The corners resize; anything else moves.
    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (sender is not Border { DataContext: VideoOverlaySlotViewModel { Layer: { } layer } } border ||
            DataContext is not VideoOverlayViewModel model ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(border);
        var handle = Math.Min(18, Math.Min(border.Bounds.Width, border.Bounds.Height) / 4);
        _dragMode = point.X < handle
            ? point.Y < handle ? VideoOverlayManipulationMode.TopLeft : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomLeft : VideoOverlayManipulationMode.Move
            : point.X > border.Bounds.Width - handle
                ? point.Y < handle ? VideoOverlayManipulationMode.TopRight : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomRight : VideoOverlayManipulationMode.Move
                : VideoOverlayManipulationMode.Move;
        _dragLayer = layer;
        _lastPointer = e.GetPosition(PreviewCanvas);
        model.SelectLayer(layer);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragLayer is null && DataContext is VideoOverlayViewModel model)
        {
            model.DeselectLayer();
            Focus();
        }
    }

    private void Section_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Escape is itself a legal overlay key, so while listening it must be the
        // way out of a mode that is swallowing every key rather than adding itself.
        if (Model is { IsListening: true } listening) listening.StopListening();
        else (DataContext as VideoOverlayViewModel)?.DeselectLayer();
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLayer is null || DataContext is not VideoOverlayViewModel model) return;
        var point = e.GetPosition(PreviewCanvas);
        var size = PreviewCanvas.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        model.Manipulate(_dragLayer, _dragMode, (point.X - _lastPointer.X) / size.Width, (point.Y - _lastPointer.Y) / size.Height);
        _lastPointer = point;
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e) { EndDrag(); e.Pointer.Capture(null); }
    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();
    private void ResetCamera_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Camera");
    private void ResetKeyboard_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Keyboard");
    private void EndDrag() { if (_dragLayer is null) return; (DataContext as VideoOverlayViewModel)?.CommitManipulation(); _dragLayer = null; }
    // The windows are laid out in canvas pixels, so the view model measures in
    // the canvas the user is actually looking at rather than a fixed 960x540.
    private void PreviewCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        (DataContext as VideoOverlayViewModel)?.SetPreviewSize(e.NewSize.Width, e.NewSize.Height);

    private void UpdateNarrowLayout()
    {
        if (DataContext is VideoOverlayViewModel model && PreviewCanvas.Bounds is { Width: > 0, Height: > 0 } bounds)
            model.SetPreviewSize(bounds.Width, bounds.Height);
        WidePreview.IsVisible = Bounds.Width >= 600;
        NarrowPickers.IsVisible = Bounds.Width < 600;
    }
}
