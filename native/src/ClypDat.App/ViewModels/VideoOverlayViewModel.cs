using System.Collections.ObjectModel;
using System.Diagnostics;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

// Deliberately separate from MainWindowViewModel: camera probing can block on
// a broken USB driver and must never make settings navigation stall.
public sealed class VideoOverlayViewModel : ViewModelBase
{
    private readonly VideoOverlaySettings _settings;
    private readonly Action _save;
    private string _sourceStatus = "No sources selected.";
    private bool _previewVisible;
    private CameraOption? _selectedCamera;
    private string _selectedKeyboardLayout;

    public VideoOverlayViewModel(VideoOverlaySettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        Cameras = new ObservableCollection<CameraOption> { CameraOption.None };
        KeyboardLayouts = new ObservableCollection<string>(new[] { "None", "QWERTY Compact", "QWERTY Full", "Arrows", "AZERTY Compact" });
        _selectedKeyboardLayout = KeyboardLayouts.Contains(settings.KeyboardLayout) ? settings.KeyboardLayout : "None";
        _selectedCamera = CameraOption.None;
        _ = RefreshCamerasAsync();
        UpdateStatus();
    }

    public ObservableCollection<CameraOption> Cameras { get; }
    public ObservableCollection<string> KeyboardLayouts { get; }
    public bool Enabled { get => _settings.Enabled; set { if (_settings.Enabled == value) return; _settings.Enabled = value; Save(); } }
    public bool IncludeVirtualCameras { get => _settings.IncludeVirtualCameras; set { if (_settings.IncludeVirtualCameras == value) return; _settings.IncludeVirtualCameras = value; _ = RefreshCamerasAsync(); Save(); } }
    public bool PreviewVisible { get => _previewVisible; set { if (!SetProperty(ref _previewVisible, value)) return; UpdateStatus(); } }
    public string PreviewButtonText => PreviewVisible ? "Hide Preview" : "Show Preview";
    public string SourceStatus { get => _sourceStatus; private set => SetProperty(ref _sourceStatus, value); }

    public CameraOption? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!SetProperty(ref _selectedCamera, value)) return;
            _settings.Camera = value?.IsNone == false ? new(value.Moniker, value.Name) : null;
            Save(); UpdateStatus();
        }
    }
    public string SelectedKeyboardLayout
    {
        get => _selectedKeyboardLayout;
        set { if (!SetProperty(ref _selectedKeyboardLayout, value)) return; _settings.KeyboardLayout = value; Save(); UpdateStatus(); }
    }

    public void TogglePreview() => PreviewVisible = !PreviewVisible;
    public void SetCorner(string corner, bool camera)
    {
        var aspect = camera ? VideoOverlayLayout.CameraAspectRatio : 2.4;
        var current = camera ? _settings.CameraTransform : _settings.KeyboardTransform;
        var next = VideoOverlayLayout.Corner(corner, current.Width, aspect);
        if (camera) _settings.CameraTransform = next; else _settings.KeyboardTransform = next;
        Save();
    }

    public async Task RefreshCamerasAsync()
    {
        var current = _settings.Camera;
        var cameras = await Task.Run(() => DirectShowCameraProbe.List(_settings.IncludeVirtualCameras));
        Cameras.Clear(); Cameras.Add(CameraOption.None);
        foreach (var camera in cameras) Cameras.Add(camera);
        _selectedCamera = current is null ? CameraOption.None : Cameras.FirstOrDefault(camera => camera.Moniker == current.DeviceMoniker) ?? CameraOption.None;
        OnPropertyChanged(nameof(SelectedCamera));
        UpdateStatus();
    }

    private void Save() { _save(); OnPropertyChanged(nameof(PreviewButtonText)); }
    private void UpdateStatus()
    {
        var camera = SelectedCamera?.IsNone == false ? $"Camera: {SelectedCamera.Name}" : "Camera: none";
        var keyboard = SelectedKeyboardLayout == "None" ? "Input: none" : $"Input: {SelectedKeyboardLayout}";
        SourceStatus = $"{camera}. {keyboard}." + (PreviewVisible ? " Preview active." : string.Empty);
    }
}

public sealed record CameraOption(string Name, string Moniker, bool IsVirtual = false)
{
    public static CameraOption None { get; } = new("None", string.Empty);
    public bool IsNone => string.IsNullOrEmpty(Moniker);
    public override string ToString() => Name;
}

internal static class DirectShowCameraProbe
{
    // FFmpeg's dshow device list is DirectShow enumeration, including device
    // names accepted by FFmpeg. Do not fall back to a different device.
    public static IReadOnlyList<CameraOption> List(bool includeVirtual)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) return Array.Empty<CameraOption>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy")
            { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true });
            if (process is null) return Array.Empty<CameraOption>();
            var text = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            var names = System.Text.RegularExpressions.Regex.Matches(text, "\\\"(?<name>[^\\\"]+)\\\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                .Select(match => match.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !name.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase));
            return names.Where(name => includeVirtual || !IsVirtual(name))
                .Select(name => new CameraOption(name, name, IsVirtual(name))).ToArray();
        }
        catch { return Array.Empty<CameraOption>(); }
    }

    private static bool IsVirtual(string name) => name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("obs", StringComparison.OrdinalIgnoreCase) || name.Contains("snap camera", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("manycam", StringComparison.OrdinalIgnoreCase) || name.Contains("ndi", StringComparison.OrdinalIgnoreCase);
}
