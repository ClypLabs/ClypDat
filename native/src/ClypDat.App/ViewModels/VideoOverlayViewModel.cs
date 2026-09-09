using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class VideoOverlayViewModel : ViewModelBase, IDisposable
{
    private readonly VideoOverlaySettings _settings;
    private readonly Action _save;
    private readonly Action? _apply;
    private string _sourceStatus = "No sources selected.";
    private string? _selectedLayer;
    private double _previewWidth = 640, _previewHeight = 360;
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshGeneration;
    private readonly ICameraPreviewService _cameraPreview;
    private WriteableBitmap? _cameraPreviewImage;
    private string? _cameraPreviewError;
    private bool _cameraPreviewLoading;
    private readonly object _previewFrameGate = new();
    private CameraPreviewFrame? _latestPreviewFrame;
    private int _previewSession;
    private bool _previewFrameQueued;
    private readonly Stopwatch _previewCadence = Stopwatch.StartNew();
    private long _appliedPreviewFrames;

    public event Action? CameraPreviewFrameUpdated;

    public VideoOverlayViewModel(VideoOverlaySettings settings, Action save, Action? apply = null)
        : this(settings, save, apply, new CameraPreviewService()) { }

    internal VideoOverlayViewModel(VideoOverlaySettings settings, Action save, Action? apply, ICameraPreviewService cameraPreview)
    {
        _settings = settings; _save = save; _apply = apply;
        _cameraPreview = cameraPreview;
        _cameraPreview.FrameReady += CameraPreview_FrameReady;
        _cameraPreview.Failed += CameraPreview_Failed;
        Cameras = new() { CameraOption.None }; Sources = new();
        // Before RebuildSources: it notifies layout, and layout refreshes slots.
        Slots = new(VideoOverlayLayout.Corners.Select(corner => new VideoOverlaySlotViewModel(this, corner)));
        RebuildSources();
        _ = RefreshCamerasAsync(); UpdateStatus();
    }

    public ObservableCollection<CameraOption> Cameras { get; }
    public ObservableCollection<OverlaySourceOption> Sources { get; }
    /// <summary>The four corner windows drawn over the preview canvas.</summary>
    public ObservableCollection<VideoOverlaySlotViewModel> Slots { get; }
    public bool IncludeVirtualCameras { get => _settings.IncludeVirtualCameras; set { if (_settings.IncludeVirtualCameras == value) return; _settings.IncludeVirtualCameras = value; _ = RefreshCamerasAsync(); Save(); } }
    public string SourceStatus { get => _sourceStatus; private set => SetProperty(ref _sourceStatus, value); }
    public bool HasCamera => _settings.Camera is not null;
    public bool HasKeyboard => _settings.KeyboardLayout != "None";
    public string KeyboardLayout => _settings.KeyboardLayout;
    public bool CameraSelected => _selectedLayer == "Camera";
    public bool KeyboardSelected => _selectedLayer == "Keyboard";
    public bool IsPositioning => _selectedLayer is not null;
    public bool ShowPickers => true;
    public Bitmap? CameraPreviewImage => _cameraPreviewImage;
    public string CameraPreviewError { get => _cameraPreviewError ?? string.Empty; private set => SetProperty(ref _cameraPreviewError, value); }
    public bool CameraPreviewLoading { get => _cameraPreviewLoading; private set => SetProperty(ref _cameraPreviewLoading, value); }
    public bool CameraPreviewActive => _cameraPreviewImage is not null && _cameraPreview.IsRunning;
    public bool CameraPreviewIdle => !CameraPreviewLoading && !CameraPreviewActive && string.IsNullOrEmpty(CameraPreviewError);
    public bool CameraPreviewFailed => !string.IsNullOrEmpty(CameraPreviewError);
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

    internal double PreviewHeight => _previewHeight;
    internal bool IsLayerSelected(string layer) => _selectedLayer == layer;

    // A window with a source sits where that layer will burn in. An empty
    // corner has no transform to sit on, so it parks a placeholder at its own
    // corner until something is chosen.
    private const double EmptySlotWidth = .22;
    internal Rect SlotRect(string corner)
    {
        if (_settings.CameraAnchor == corner && HasCamera) return LayerRect(_settings.CameraTransform, SourceAspect("Camera"));
        if (_settings.KeyboardAnchor == corner && HasKeyboard) return LayerRect(_settings.KeyboardTransform, SourceAspect("Keyboard"));
        var aspect = VideoOverlayLayout.CameraAspectRatio;
        return LayerRect(VideoOverlayLayout.Corner(corner, EmptySlotWidth, aspect / (_previewWidth / _previewHeight)), aspect);
    }
    private Rect LayerRect(VideoOverlayTransform transform, double aspect)
    {
        var width = transform.Width * _previewWidth;
        return new Rect(transform.X * _previewWidth, transform.Y * _previewHeight, width, width / aspect);
    }

    public void ClosePreview() { StopCameraPreview(); DeselectLayer(); }
    public void DeselectLayer() { _selectedLayer = null; NotifyLayout(); }
    public void SetPreviewSize(double width, double height) { if (width <= 0 || height <= 0) return; _previewWidth = width; _previewHeight = height; NotifyLayout(); }
    public void SelectLayer(string layer) { _selectedLayer = layer; NotifyLayout(); }
    public void StartCameraPreview()
    {
        if (_settings.Camera is not { } camera) return;
        StopCameraPreview();
        var previous = CapturePreviewState();
        CameraPreviewError = string.Empty; CameraPreviewLoading = true; NotifyPreviewState(previous);
        _cameraPreview.Start(camera.DeviceMoniker);
        Volatile.Write(ref _previewSession, _cameraPreview.Session);
    }
    public void StopCameraPreview()
    {
        var previous = CapturePreviewState();
        Volatile.Write(ref _previewSession, 0);
        lock (_previewFrameGate) { _latestPreviewFrame?.Dispose(); _latestPreviewFrame = null; _previewFrameQueued = false; }
        _cameraPreview.Stop(); CameraPreviewLoading = false;
        if (_cameraPreviewImage is not null) { _cameraPreviewImage.Dispose(); _cameraPreviewImage = null; }
        NotifyPreviewState(previous);
    }
    public void Manipulate(string layer, VideoOverlayManipulationMode mode, double deltaX, double deltaY)
    {
        var current = layer == "Camera" ? _settings.CameraTransform : _settings.KeyboardTransform;
        var next = VideoOverlayManipulation.Apply(current, mode, deltaX, deltaY, _previewWidth / _previewHeight, SourceAspect(layer));
        if (layer == "Camera") _settings.CameraTransform = next; else _settings.KeyboardTransform = next;
        _apply?.Invoke();
        NotifyLayout();
    }
    public void CommitManipulation() { Save(); UpdateStatus(); }
    public void ResetToCorner(string layer)
    {
        if (layer == "Camera" && HasCamera && _settings.CameraAnchor is { } cameraCorner)
            _settings.CameraTransform = VideoOverlayLayout.Corner(cameraCorner, _settings.CameraTransform.Width, NormalizedAspect(layer));
        else if (layer == "Keyboard" && HasKeyboard && _settings.KeyboardAnchor is { } keyboardCorner)
            _settings.KeyboardTransform = VideoOverlayLayout.Corner(keyboardCorner, _settings.KeyboardTransform.Width, NormalizedAspect(layer));
        else return;
        Save(); UpdateStatus(); NotifyLayout();
    }
    public async Task RefreshCamerasAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var previous = Interlocked.Exchange(ref _refreshCancellation, new CancellationTokenSource());
        previous?.Cancel(); previous?.Dispose();
        var cancellation = _refreshCancellation!;
        IReadOnlyList<CameraOption> cameras;
        try
        {
            cameras = await Task.Run(() => DirectShowCameraProbe.List(_settings.IncludeVirtualCameras, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) { return; }
        if (generation != _refreshGeneration || cancellation.IsCancellationRequested) return;
        ReconcileCameras(cameras);
        RepairSavedElgatoVirtualCamera(cameras);
        // Device disappearance is transient. Keep selection and transform so it
        // returns in same place when USB camera reconnects.
        if (_settings.Camera is { } selected &&
            DirectShowCameraParser.IsSavedCameraSelection(selected) &&
            !Cameras.Any(camera => camera.Moniker == selected.DeviceMoniker))
            Cameras.Add(new CameraOption($"{selected.FriendlyName} (unavailable)", selected.DeviceMoniker));
        RebuildSources(); UpdateStatus(); NotifyLayout();
    }
    public void SelectSourceAt(string corner)
    {
        var source = SourceAt(corner);
        if (source?.Kind == OverlaySourceKind.Camera) SelectLayer("Camera");
        else if (source?.Kind == OverlaySourceKind.Keyboard) SelectLayer("Keyboard");
    }
    internal OverlaySourceOption? SourceAt(string corner)
    {
        if (_settings.CameraAnchor == corner && HasCamera)
            return Sources.FirstOrDefault(source => source.Kind == OverlaySourceKind.Camera && source.Value == _settings.Camera!.DeviceMoniker);
        if (_settings.KeyboardAnchor == corner && HasKeyboard) return Sources.FirstOrDefault(source => source.Kind == OverlaySourceKind.Keyboard && source.Value == _settings.KeyboardLayout);
        return OverlaySourceOption.None;
    }
    internal void SetSource(string corner, OverlaySourceOption? source)
    {
        if (source is null || !source.IsSelectable) return;
        if (source.Kind == OverlaySourceKind.None)
        {
            if (_settings.CameraAnchor == corner) { StopCameraPreview(); _settings.Camera = null; _settings.CameraAnchor = null; }
            if (_settings.KeyboardAnchor == corner) { _settings.KeyboardLayout = "None"; _settings.KeyboardAnchor = null; }
        }
        else if (source.Kind == OverlaySourceKind.Camera)
        {
            if (_settings.Camera?.DeviceMoniker != source.Value) StopCameraPreview();
            if (_settings.KeyboardAnchor == corner) { _settings.KeyboardLayout = "None"; _settings.KeyboardAnchor = null; }
            _settings.Camera = new(source.Value, source.Name);
            _settings.CameraAnchor = corner;
            _settings.CameraTransform = VideoOverlayLayout.Corner(corner, .25, NormalizedAspect("Camera"));
            _selectedLayer = "Camera";
        }
        else
        {
            if (_settings.CameraAnchor == corner) { StopCameraPreview(); _settings.Camera = null; _settings.CameraAnchor = null; }
            _settings.KeyboardLayout = source.Value;
            _settings.KeyboardAnchor = corner;
            _settings.KeyboardTransform = VideoOverlayLayout.Corner(corner, .25, NormalizedAspect("Keyboard"));
            _selectedLayer = "Keyboard";
        }
        Save(); UpdateStatus(); NotifyLayout();
    }
    private void RebuildSources()
    {
        var next = OverlaySourceOptions.Create(Cameras).ToArray();
        for (var index = Sources.Count - 1; index >= 0; index--) if (!next.Contains(Sources[index])) Sources.RemoveAt(index);
        for (var index = 0; index < next.Length; index++) {
            if (index < Sources.Count && Sources[index] == next[index]) continue;
            var existing = Sources.IndexOf(next[index]);
            if (existing >= 0) Sources.Move(existing, index); else Sources.Insert(index, next[index]);
        }
        NotifyLayout();
    }
    private void ReconcileCameras(IReadOnlyList<CameraOption> cameras)
    {
        var next = new[] { CameraOption.None }.Concat(cameras).ToArray();
        for (var index = Cameras.Count - 1; index >= 0; index--) if (!next.Contains(Cameras[index])) Cameras.RemoveAt(index);
        for (var index = 0; index < next.Length; index++) {
            if (index < Cameras.Count && Cameras[index] == next[index]) continue;
            var existing = Cameras.IndexOf(next[index]);
            if (existing >= 0) Cameras.Move(existing, index); else Cameras.Insert(index, next[index]);
        }
    }
    private void RepairSavedElgatoVirtualCamera(IReadOnlyList<CameraOption> cameras)
    {
        if (_settings.Camera is not { } saved) return;
        var repaired = DirectShowCameraParser.RepairSavedElgatoVirtualCamera(saved, cameras);
        if (repaired is null) return;
        _settings.Camera = repaired;
        Save();
    }
    private double SourceAspect(string layer) => layer == "Camera" ? VideoOverlayLayout.CameraAspectRatio : KeyboardOverlayCatalog.Get(_settings.KeyboardLayout).AspectRatio;
    private double NormalizedAspect(string layer) => SourceAspect(layer) / (_previewWidth / _previewHeight);
    private static bool AtAnchor(VideoOverlayTransform transform, string? corner, double aspect) { if (corner is null) return false; var anchor = VideoOverlayLayout.Corner(corner, transform.Width, aspect); return Math.Abs(transform.X - anchor.X) < .002 && Math.Abs(transform.Y - anchor.Y) < .002; }
    private void Save() { _save(); _apply?.Invoke(); }
    private void CameraPreview_Failed(CameraPreviewFailure failure) => Dispatcher.UIThread.Post(() =>
    {
        if (failure.Session != Volatile.Read(ref _previewSession)) return;
        var previous = CapturePreviewState(); CameraPreviewLoading = false; CameraPreviewError = failure.Message; NotifyPreviewState(previous);
    });
    private void CameraPreview_FrameReady(CameraPreviewFrame frame)
    {
        if (frame.Session != Volatile.Read(ref _previewSession)) { frame.Dispose(); return; }
        lock (_previewFrameGate)
        {
            _latestPreviewFrame?.Dispose();
            _latestPreviewFrame = frame;
            if (_previewFrameQueued) return;
            _previewFrameQueued = true;
        }
        Dispatcher.UIThread.Post(() => ApplyLatestPreviewFrame(frame.Session), DispatcherPriority.Render);
    }
    private void ApplyLatestPreviewFrame(int session)
    {
        CameraPreviewFrame? frame;
        lock (_previewFrameGate)
        {
            if (session != Volatile.Read(ref _previewSession)) { _latestPreviewFrame?.Dispose(); _latestPreviewFrame = null; _previewFrameQueued = false; return; }
            frame = _latestPreviewFrame;
            _latestPreviewFrame = null;
            _previewFrameQueued = false;
        }
        if (frame is null || !_cameraPreview.IsRunning) { frame?.Dispose(); return; }
        var previous = CapturePreviewState();
        try
        {
            _cameraPreviewImage ??= new WriteableBitmap(new PixelSize(CameraPreviewService.Width, CameraPreviewService.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var locked = _cameraPreviewImage.Lock())
            {
                unsafe { fixed (byte* source = frame.Pixels) Buffer.MemoryCopy(source, (void*)locked.Address, CameraPreviewService.FrameBytes, CameraPreviewService.FrameBytes); }
            }
        }
        finally { frame.Dispose(); }
        CameraPreviewLoading = false;
        CameraPreviewError = string.Empty;
        NotifyPreviewState(previous);
        CameraPreviewFrameUpdated?.Invoke();
        _appliedPreviewFrames++;
        if (_previewCadence.Elapsed >= TimeSpan.FromSeconds(5))
        {
            AppLog.Debug($"Camera preview cadence: applied={_appliedPreviewFrames}, seconds={_previewCadence.Elapsed.TotalSeconds:0.0}.");
            _appliedPreviewFrames = 0;
            _previewCadence.Restart();
        }
    }
    private readonly record struct PreviewState(Bitmap? Image, bool Loading, bool Active, bool Idle, bool Failed, string Error);
    private PreviewState CapturePreviewState() => new(CameraPreviewImage, CameraPreviewLoading, CameraPreviewActive, CameraPreviewIdle, CameraPreviewFailed, CameraPreviewError);
    private void NotifyPreviewState(PreviewState previous)
    {
        var current = CapturePreviewState();
        if (!ReferenceEquals(previous.Image, current.Image)) OnPropertyChanged(nameof(CameraPreviewImage));
        if (previous.Active != current.Active) OnPropertyChanged(nameof(CameraPreviewActive));
        if (previous.Idle != current.Idle) OnPropertyChanged(nameof(CameraPreviewIdle));
        if (previous.Failed != current.Failed) OnPropertyChanged(nameof(CameraPreviewFailed));
    }
    private void NotifyLayout()
    {
        foreach (var name in new[] { nameof(HasCamera), nameof(HasKeyboard), nameof(KeyboardLayout), nameof(CameraSelected), nameof(KeyboardSelected), nameof(IsPositioning), nameof(ShowPickers), nameof(CameraCustomPosition), nameof(KeyboardCustomPosition), nameof(CameraPositionHint), nameof(KeyboardPositionHint), nameof(CameraLeft), nameof(CameraTop), nameof(CameraWidth), nameof(CameraHeight), nameof(KeyboardLeft), nameof(KeyboardTop), nameof(KeyboardWidth), nameof(KeyboardHeight), nameof(TopLeftSource), nameof(TopRightSource), nameof(BottomLeftSource), nameof(BottomRightSource) }) OnPropertyChanged(name);
        foreach (var slot in Slots) slot.Refresh();
    }
    private void UpdateStatus() { var camera = HasCamera ? $"Camera: {_settings.Camera!.FriendlyName}" : "Camera: none"; var keyboard = HasKeyboard ? $"Input: {_settings.KeyboardLayout}" : "Input: none"; SourceStatus = $"{camera}. {keyboard}."; }
    public void Dispose() { _refreshCancellation?.Cancel(); _refreshCancellation?.Dispose(); StopCameraPreview(); _cameraPreview.FrameReady -= CameraPreview_FrameReady; _cameraPreview.Failed -= CameraPreview_Failed; _cameraPreview.Dispose(); }
}
public enum OverlaySourceKind { None, Camera, Keyboard, Heading }
public sealed record OverlaySourceOption(string Name, string Value, OverlaySourceKind Kind)
{
    public static OverlaySourceOption None { get; } = new("None", string.Empty, OverlaySourceKind.None);
    public static OverlaySourceOption Heading(string name) => new(name, string.Empty, OverlaySourceKind.Heading);
    public bool IsSelectable => Kind != OverlaySourceKind.Heading;
    public bool IsHeading => Kind == OverlaySourceKind.Heading;
    public override string ToString() => Name;
}
internal static class OverlaySourceOptions
{
    public static IReadOnlyList<OverlaySourceOption> Create(IEnumerable<CameraOption> cameraOptions)
    {
        var sources = new List<OverlaySourceOption> { OverlaySourceOption.None };
        var cameras = cameraOptions.Where(camera => !camera.IsNone).ToArray();
        if (cameras.Length > 0)
        {
            sources.Add(OverlaySourceOption.Heading("Cameras"));
            sources.AddRange(cameras.Select(camera => new OverlaySourceOption(camera.Name, camera.Moniker, OverlaySourceKind.Camera)));
        }
        sources.Add(OverlaySourceOption.Heading("Peripheral Overlays"));
        sources.AddRange(new[] {
            new OverlaySourceOption("QWERTY Keyboard + Mouse", "QWERTY Compact", OverlaySourceKind.Keyboard),
            new OverlaySourceOption("QWERTY Keyboard + Mouse (Full)", "QWERTY Full", OverlaySourceKind.Keyboard),
            new OverlaySourceOption("Arrow Keys + Mouse", "Arrows", OverlaySourceKind.Keyboard),
            new OverlaySourceOption("AZERTY Keyboard + Mouse", "AZERTY Compact", OverlaySourceKind.Keyboard) });
        return sources;
    }
}
public sealed record CameraOption(string Name, string Moniker, bool IsVirtual = false) { public static CameraOption None { get; } = new("None", string.Empty); public bool IsNone => string.IsNullOrEmpty(Moniker); }
internal static class DirectShowCameraProbe
{
    public static IReadOnlyList<CameraOption> List(bool includeVirtual, CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"); if (!File.Exists(ffmpeg)) return Array.Empty<CameraOption>();
        try { using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy") { UseShellExecute = false, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true }); if (process is null) return Array.Empty<CameraOption>(); var read = process.StandardError.ReadToEndAsync(cancellationToken); if (!process.WaitForExit(5000)) { try { process.Kill(true); } catch { } return Array.Empty<CameraOption>(); } var text = read.GetAwaiter().GetResult(); cancellationToken.ThrowIfCancellationRequested(); return DirectShowCameraParser.Parse(text, includeVirtual); } catch (OperationCanceledException) { throw; } catch { return Array.Empty<CameraOption>(); }
    }
}

internal static class DirectShowCameraParser
{
    private static readonly System.Text.RegularExpressions.Regex VideoDevice = new(
        "^\\s*\\[[^\\]]*\\]\\s+\"(?<name>[^\"]+)\"\\s+\\(video\\)\\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static IReadOnlyList<CameraOption> Parse(string output, bool includeVirtual)
    {
        return output.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => VideoDevice.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !IsExcludedDevice(name))
            .Where(name => includeVirtual || !IsVirtual(name))
            .Select(name => new CameraOption(DisplayName(name), name, IsVirtual(name)))
            .ToArray();
    }

    public static bool IsElgatoVirtualCamera(string name) =>
        name.Equals("EƖgato Virtual Camera", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Elgato Virtual Camera", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("EÆ–gato Virtual Camera", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("EÆ\u0096gato Virtual Camera", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("E�gato Virtual Camera", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string name) => IsElgatoVirtualCamera(name) ? "Elgato Virtual Camera" : name;

    public static bool IsSavedCameraSelection(VideoOverlayCameraSelection selection) =>
        !IsRawDeviceIdentifier(selection.DeviceMoniker) &&
        !IsRawDeviceIdentifier(selection.FriendlyName) &&
        !IsExcludedDevice(selection.FriendlyName);

    public static VideoOverlayCameraSelection? RepairSavedElgatoVirtualCamera(
        VideoOverlayCameraSelection saved, IReadOnlyList<CameraOption> cameras)
    {
        if (!IsElgatoVirtualCamera(saved.DeviceMoniker) && !IsElgatoVirtualCamera(saved.FriendlyName)) return null;
        var detected = cameras.FirstOrDefault(camera => IsElgatoVirtualCamera(camera.Moniker));
        if (detected is null) return null;
        var repaired = new VideoOverlayCameraSelection(detected.Moniker, detected.Name);
        return saved == repaired ? null : repaired;
    }

    private static bool IsVirtual(string name) => name.Contains("virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("obs", StringComparison.OrdinalIgnoreCase) || name.Contains("snap camera", StringComparison.OrdinalIgnoreCase) || name.Contains("manycam", StringComparison.OrdinalIgnoreCase) || name.Contains("ndi", StringComparison.OrdinalIgnoreCase);
    private static bool IsRawDeviceIdentifier(string value) => value.StartsWith("@device_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase);
    private static bool IsExcludedDevice(string name) => name.Contains("audio", StringComparison.OrdinalIgnoreCase) || name.Contains("microphone", StringComparison.OrdinalIgnoreCase) || name.Contains("mic", StringComparison.OrdinalIgnoreCase) || name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase) || name.Contains("speaker", StringComparison.OrdinalIgnoreCase) || name.Contains("headphone", StringComparison.OrdinalIgnoreCase) || name.Contains("headset", StringComparison.OrdinalIgnoreCase) || name.Contains("stereo mix", StringComparison.OrdinalIgnoreCase) || name.Contains("line in", StringComparison.OrdinalIgnoreCase);
}
