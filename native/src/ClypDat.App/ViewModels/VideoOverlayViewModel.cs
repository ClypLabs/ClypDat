using System.Collections.ObjectModel;
using System.Diagnostics;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class VideoOverlayViewModel : ViewModelBase
{
    private readonly VideoOverlaySettings _settings;
    private readonly Action _save;
    private readonly Action? _apply;
    private string _sourceStatus = "No sources selected.";
    private bool _previewVisible;
    private string? _selectedLayer;
    private double _previewWidth = 640, _previewHeight = 360;

    public VideoOverlayViewModel(VideoOverlaySettings settings, Action save, Action? apply = null)
    {
        _settings = settings; _save = save; _apply = apply;
        Cameras = new() { CameraOption.None }; Sources = new(); RebuildSources();
        _ = RefreshCamerasAsync(); UpdateStatus();
    }

    public ObservableCollection<CameraOption> Cameras { get; }
    public ObservableCollection<OverlaySourceOption> Sources { get; }
    public bool Enabled { get => _settings.Enabled; set { if (_settings.Enabled == value) return; _settings.Enabled = value; Save(); } }
    public bool IncludeVirtualCameras { get => _settings.IncludeVirtualCameras; set { if (_settings.IncludeVirtualCameras == value) return; _settings.IncludeVirtualCameras = value; _ = RefreshCamerasAsync(); Save(); } }
    public bool PreviewVisible { get => _previewVisible; set { if (!SetProperty(ref _previewVisible, value)) return; OnPropertyChanged(nameof(PreviewButtonText)); UpdateStatus(); } }
    public string PreviewButtonText => PreviewVisible ? "Hide Preview" : "Show Preview";
    public string SourceStatus { get => _sourceStatus; private set => SetProperty(ref _sourceStatus, value); }
    public bool HasCamera => _settings.Camera is not null;
    public bool HasKeyboard => _settings.KeyboardLayout != "None";
    public bool CameraSelected => _selectedLayer == "Camera";
    public bool KeyboardSelected => _selectedLayer == "Keyboard";
    public bool CameraCustomPosition => HasCamera && !AtAnchor(_settings.CameraTransform, _settings.CameraAnchor, NormalizedAspect("Camera"));
    public bool KeyboardCustomPosition => HasKeyboard && !AtAnchor(_settings.KeyboardTransform, _settings.KeyboardAnchor, NormalizedAspect("Keyboard"));
    public string CameraPositionHint => CameraCustomPosition ? "Custom position" : _settings.CameraAnchor ?? string.Empty;
    public string KeyboardPositionHint => KeyboardCustomPosition ? "Custom position" : _settings.KeyboardAnchor ?? string.Empty;
    public double CameraLeft => _settings.CameraTransform.X * _previewWidth;
    public double CameraTop => _settings.CameraTransform.Y * _previewHeight;
    public double CameraWidth => _settings.CameraTransform.Width * _previewWidth;
    public double CameraHeight => _settings.CameraTransform.Width * _previewWidth / SourceAspect("Camera");
    public double KeyboardLeft => _settings.KeyboardTransform.X * _previewWidth;
    public double KeyboardTop => _settings.KeyboardTransform.Y * _previewHeight;
    public double KeyboardWidth => _settings.KeyboardTransform.Width * _previewWidth;
    public double KeyboardHeight => _settings.KeyboardTransform.Width * _previewWidth / SourceAspect("Keyboard");
    public OverlaySourceOption? TopLeftSource { get => SourceAt("Top Left"); set => SetSource("Top Left", value); }
    public OverlaySourceOption? TopRightSource { get => SourceAt("Top Right"); set => SetSource("Top Right", value); }
    public OverlaySourceOption? BottomLeftSource { get => SourceAt("Bottom Left"); set => SetSource("Bottom Left", value); }
    public OverlaySourceOption? BottomRightSource { get => SourceAt("Bottom Right"); set => SetSource("Bottom Right", value); }

    public void TogglePreview() => PreviewVisible = !PreviewVisible;
    public void ClosePreview() { if (PreviewVisible) PreviewVisible = false; _selectedLayer = null; NotifyLayout(); }
    public void SetPreviewSize(double width, double height) { if (width <= 0 || height <= 0) return; _previewWidth = width; _previewHeight = height; NotifyLayout(); }
    public void SelectLayer(string layer) { _selectedLayer = layer; NotifyLayout(); }
    public void Manipulate(string layer, VideoOverlayManipulationMode mode, double deltaX, double deltaY)
    {
        var current = layer == "Camera" ? _settings.CameraTransform : _settings.KeyboardTransform;
        var next = VideoOverlayManipulation.Apply(current, mode, deltaX, deltaY, _previewWidth / _previewHeight, SourceAspect(layer));
        if (layer == "Camera") _settings.CameraTransform = next; else _settings.KeyboardTransform = next;
        NotifyLayout();
    }
    public void CommitManipulation() { Save(); UpdateStatus(); }
    public async Task RefreshCamerasAsync()
    {
        var cameras = await Task.Run(() => DirectShowCameraProbe.List(_settings.IncludeVirtualCameras));
        Cameras.Clear(); Cameras.Add(CameraOption.None); foreach (var camera in cameras) Cameras.Add(camera);
        var removedCamera = _settings.Camera is not null && !Cameras.Any(camera => camera.Moniker == _settings.Camera.DeviceMoniker);
        if (removedCamera) { _settings.Camera = null; _settings.CameraAnchor = null; Save(); }
        RebuildSources(); UpdateStatus(); NotifyLayout();
    }
    private OverlaySourceOption? SourceAt(string corner)
    {
        if (_settings.CameraAnchor == corner && HasCamera)
            return Sources.FirstOrDefault(source => source.Kind == OverlaySourceKind.Camera && source.Value == _settings.Camera!.DeviceMoniker);
        if (_settings.KeyboardAnchor == corner && HasKeyboard) return Sources.FirstOrDefault(source => source.Kind == OverlaySourceKind.Keyboard && source.Value == _settings.KeyboardLayout);
        return OverlaySourceOption.None;
    }
    private void SetSource(string corner, OverlaySourceOption? source)
    {
        if (source is null) return;
        if (source.Kind == OverlaySourceKind.None)
        {
            if (_settings.CameraAnchor == corner) { _settings.Camera = null; _settings.CameraAnchor = null; }
            if (_settings.KeyboardAnchor == corner) { _settings.KeyboardLayout = "None"; _settings.KeyboardAnchor = null; }
        }
        else if (source.Kind == OverlaySourceKind.Camera)
        {
            if (_settings.KeyboardAnchor == corner) { _settings.KeyboardLayout = "None"; _settings.KeyboardAnchor = null; }
            _settings.Camera = new(source.Value, source.Name);
            _settings.CameraAnchor = corner;
            _settings.CameraTransform = VideoOverlayLayout.Corner(corner, .25, NormalizedAspect("Camera"));
            _selectedLayer = "Camera";
        }
        else
        {
            if (_settings.CameraAnchor == corner) { _settings.Camera = null; _settings.CameraAnchor = null; }
            _settings.KeyboardLayout = source.Value;
            _settings.KeyboardAnchor = corner;
            _settings.KeyboardTransform = VideoOverlayLayout.Corner(corner, .25, NormalizedAspect("Keyboard"));
            _selectedLayer = "Keyboard";
        }
        Save(); UpdateStatus(); NotifyLayout();
    }
    private void RebuildSources()
    {
        Sources.Clear(); Sources.Add(OverlaySourceOption.None);
        foreach (var camera in Cameras.Where(camera => !camera.IsNone)) Sources.Add(new(camera.Name, camera.Moniker, OverlaySourceKind.Camera));
        foreach (var layout in new[] { "QWERTY Compact", "QWERTY Full", "Arrows", "AZERTY Compact" }) Sources.Add(new(layout, layout, OverlaySourceKind.Keyboard));
        OnPropertyChanged(nameof(TopLeftSource)); OnPropertyChanged(nameof(TopRightSource)); OnPropertyChanged(nameof(BottomLeftSource)); OnPropertyChanged(nameof(BottomRightSource));
    }
    private double SourceAspect(string layer) => layer == "Camera" ? VideoOverlayLayout.CameraAspectRatio : 2.4;
    private double NormalizedAspect(string layer) => SourceAspect(layer) / (_previewWidth / _previewHeight);
    private static bool AtAnchor(VideoOverlayTransform transform, string? corner, double aspect) { if (corner is null) return false; var anchor = VideoOverlayLayout.Corner(corner, transform.Width, aspect); return Math.Abs(transform.X - anchor.X) < .002 && Math.Abs(transform.Y - anchor.Y) < .002; }
    private void Save() { _save(); _apply?.Invoke(); }
    private void NotifyLayout()
    {
        foreach (var name in new[] { nameof(HasCamera), nameof(HasKeyboard), nameof(CameraSelected), nameof(KeyboardSelected), nameof(CameraCustomPosition), nameof(KeyboardCustomPosition), nameof(CameraPositionHint), nameof(KeyboardPositionHint), nameof(CameraLeft), nameof(CameraTop), nameof(CameraWidth), nameof(CameraHeight), nameof(KeyboardLeft), nameof(KeyboardTop), nameof(KeyboardWidth), nameof(KeyboardHeight), nameof(TopLeftSource), nameof(TopRightSource), nameof(BottomLeftSource), nameof(BottomRightSource) }) OnPropertyChanged(name);
    }
    private void UpdateStatus() { var camera = HasCamera ? $"Camera: {_settings.Camera!.FriendlyName}" : "Camera: none"; var keyboard = HasKeyboard ? $"Input: {_settings.KeyboardLayout}" : "Input: none"; SourceStatus = $"{camera}. {keyboard}." + (PreviewVisible ? " Preview active." : string.Empty); }
}
public enum OverlaySourceKind { None, Camera, Keyboard }
public sealed record OverlaySourceOption(string Name, string Value, OverlaySourceKind Kind) { public static OverlaySourceOption None { get; } = new("None", string.Empty, OverlaySourceKind.None); public override string ToString() => Name; }
public sealed record CameraOption(string Name, string Moniker, bool IsVirtual = false) { public static CameraOption None { get; } = new("None", string.Empty); public bool IsNone => string.IsNullOrEmpty(Moniker); }
internal static class DirectShowCameraProbe
{
    public static IReadOnlyList<CameraOption> List(bool includeVirtual)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"); if (!File.Exists(ffmpeg)) return Array.Empty<CameraOption>();
        try { using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true }); if (process is null) return Array.Empty<CameraOption>(); var text = process.StandardError.ReadToEnd(); process.WaitForExit(5000); return System.Text.RegularExpressions.Regex.Matches(text, "\\\"(?<name>[^\\\"]+)\\\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant).Select(match => match.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Where(name => !name.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase)).Where(name => includeVirtual || !IsVirtual(name)).Select(name => new CameraOption(name, name, IsVirtual(name))).ToArray(); } catch { return Array.Empty<CameraOption>(); }
    }
    private static bool IsVirtual(string name) => name.Contains("virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("obs", StringComparison.OrdinalIgnoreCase) || name.Contains("snap camera", StringComparison.OrdinalIgnoreCase) || name.Contains("manycam", StringComparison.OrdinalIgnoreCase) || name.Contains("ndi", StringComparison.OrdinalIgnoreCase);
}
