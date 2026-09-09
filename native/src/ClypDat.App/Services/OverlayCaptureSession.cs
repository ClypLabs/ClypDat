using System.Diagnostics;
using System.Text.Json;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Owns camera capture for one recorder lifetime.  The UI never opens the
/// device: it can only ask the worker for a preview.  Small independently
/// playable files make an in-progress replay window safe to save.
/// </summary>
internal sealed class OverlayCaptureSession : IDisposable
{
    private const int SegmentSeconds = 2;
    private readonly object _gate = new();
    private readonly string _workRoot;
    private OverlayCaptureSettings _settings = OverlayCaptureSettings.None;
    private Process? _camera;
    private string? _cameraError;
    private bool _cameraReceivedFrames;
    private readonly Dictionary<string, CameraSegment> _cameraSegments = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _cameraWatcher;
    private readonly RawInputRecorder _input = new();

    public OverlayCaptureSession(string workRoot) => _workRoot = Path.Combine(workRoot, "overlays");

    public void Apply(OverlayCaptureSettings settings)
    {
        lock (_gate)
        {
            var changed = !Equals(_settings.Camera, settings.Camera);
            _settings = settings;
            // Camera replacement must not erase keyboard history captured by
            // the worker. Input has its own lifetime and clip-start checkpoint.
            if (changed) StopCameraUnderLock();
            if (_settings.Camera is not null && _camera is null) StartCameraUnderLock();
        }
    }

    public void Start()
    {
        _input.Start();
        lock (_gate)
            if (_settings.Camera is not null && _camera is null) StartCameraUnderLock();
    }

    public void Stop() { lock (_gate) StopCameraUnderLock(); _input.Reset(); }

    public ClipOverlayLayer? FinalizeCamera(string libraryRoot, string clipPath, DateTime startUtc, DateTime endUtc)
    {
        lock (_gate)
        {
            if (_settings.Camera is not { } camera) return null;
            if (!_cameraReceivedFrames)
                return new ClipOverlayLayer(camera.FriendlyName, false, InitialTransform: _settings.CameraTransform.ToPresentationTransform(),
                    Error: _cameraError ?? "Camera did not deliver frames while this clip recorded.");

            var files = Directory.Exists(_workRoot)
                ? Directory.EnumerateFiles(_workRoot, "*.mp4").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            var destination = LibraryLayout.SidecarPath(libraryRoot, clipPath, ".camera");
            Directory.CreateDirectory(destination);
            var assets = new List<ClipOverlayAsset>();
            foreach (var file in files)
            {
                if (!TrySegmentWindow(file, out var segment) || !segment.Completed || segment.EndUtc <= startUtc || segment.StartUtc >= endUtc) continue;
                var target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, true);
                var clipStart = startUtc > segment.StartUtc ? startUtc : segment.StartUtc;
                var clipEnd = endUtc < segment.EndUtc ? endUtc : segment.EndUtc;
                assets.Add(new ClipOverlayAsset(Path.GetRelativePath(libraryRoot, target),
                    Math.Max(0, (clipStart - startUtc).TotalSeconds), Math.Min((endUtc - startUtc).TotalSeconds, (clipEnd - startUtc).TotalSeconds),
                    Math.Max(0, (clipStart - segment.StartUtc).TotalSeconds), 1));
            }
            // Sorted by clip time: the %d filenames sort lexicographically, so
            // segment 10 would otherwise precede segment 2 in the manifest.
            assets.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
            return assets.Count == 0
                ? new ClipOverlayLayer(camera.FriendlyName, false, InitialTransform: _settings.CameraTransform.ToPresentationTransform(),
                    Error: "Camera frames have not reached a completed capture segment yet.")
                : new ClipOverlayLayer(camera.FriendlyName, true, Assets: assets, InitialTransform: _settings.CameraTransform.ToPresentationTransform());
        }
    }

    public ClipOverlayLayer? FinalizeInput(string libraryRoot, string clipPath, string layout, OverlayTransform transform, DateTime startUtc, DateTime endUtc)
    {
        if (string.Equals(layout, "None", StringComparison.OrdinalIgnoreCase)) return null;
        var index = _input.Snapshot(startUtc, endUtc);
        if (index.MissingHistory is not null)
            return new ClipOverlayLayer(layout, false, InitialTransform: transform.ToPresentationTransform(), Error: "Keyboard input was not recorded.");
        try
        {
            var path = LibraryLayout.SidecarPath(libraryRoot, clipPath, ".input.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(index));
            return new ClipOverlayLayer(layout, true, InitialTransform: transform.ToPresentationTransform(), AssetPath: Path.GetRelativePath(libraryRoot, path), InputIndexPath: Path.GetRelativePath(libraryRoot, path));
        }
        catch (Exception error)
        {
            return new ClipOverlayLayer(layout, false, InitialTransform: transform.ToPresentationTransform(), Error: $"Keyboard input could not be saved: {error.Message}");
        }
    }

    private bool TrySegmentWindow(string path, out CameraSegment segment)
    {
        if (_cameraSegments.TryGetValue(path, out segment!)) return true;
        segment = default!;
        return false;
    }

    private void StartCameraUnderLock()
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { _cameraError = "FFmpeg is unavailable."; return; }
        Directory.CreateDirectory(_workRoot);
        foreach (var file in Directory.EnumerateFiles(_workRoot, "*.mp4")) AudioCapturePipeline.TryDelete(file);
        _cameraSegments.Clear();
        _cameraWatcher?.Dispose();
        _cameraWatcher = new FileSystemWatcher(_workRoot, "*.mp4") { EnableRaisingEvents = true, IncludeSubdirectories = false };
        _cameraWatcher.Created += (_, args) =>
        {
            lock (_gate)
            {
                // FFmpeg's segment muxer creates file N at the instant segment N
                // begins, so this event time measures that boundary directly.
                // Deriving it from a timestamp taken before Process.Start
                // instead charged the camera timeline with FFmpeg's spawn plus
                // the DirectShow device-open cost - hundreds of milliseconds,
                // and seconds on a virtual camera - which shifted every overlay
                // frame early against gameplay for the whole recording.
                var observed = MonotonicClock.UtcNow;
                // Closing the previous segment here also gives it its real
                // duration rather than an assumed exactly-2.000s cadence.
                foreach (var pending in _cameraSegments.Values.Where(item => !item.Completed))
                {
                    pending.EndUtc = observed;
                    pending.Completed = true;
                }
                _cameraSegments[args.FullPath] = new CameraSegment(observed, observed + TimeSpan.FromSeconds(SegmentSeconds));
            }
        };
        var pattern = Path.Combine(_workRoot, "%d.mp4");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        // Ask DirectShow for 60fps but never force an output cadence.  Forcing
        // `-r` duplicated slow cameras and hid dropped-frame gaps from replay.
        foreach (var argument in new[] { "-hide_banner", "-f", "dshow", "-framerate", "60", "-i", $"video={_settings.Camera!.DeviceMoniker}", "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-vsync", "0", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-bf", "0", "-sc_threshold", "0", "-f", "segment", "-segment_time", SegmentSeconds.ToString(), "-reset_timestamps", "1", "-segment_format_options", "movflags=+frag_keyframe+empty_moov+default_base_moof", pattern }) info.ArgumentList.Add(argument);
        try
        {
            _camera = Process.Start(info);
            if (_camera is null) { _cameraError = "Camera capture could not start."; return; }
            _ = ObserveCameraAsync(_camera);
        }
        catch (Exception error) { _cameraError = $"Camera capture could not start: {error.Message}"; }
    }

    private async Task ObserveCameraAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null) break;
                if (line.Contains("frame=", StringComparison.Ordinal)) _cameraReceivedFrames = true;
            }
            if (!_cameraReceivedFrames) _cameraError = "Camera stopped before delivering frames.";
        }
        catch (Exception error) { _cameraError = $"Camera capture failed: {error.Message}"; }
    }

    private void StopCameraUnderLock()
    {
        var process = _camera; _camera = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        process.Dispose();
    }

    public void Dispose() { Stop(); _input.Dispose(); _cameraWatcher?.Dispose(); try { if (Directory.Exists(_workRoot)) Directory.Delete(_workRoot, true); } catch { } }
    private sealed class CameraSegment(DateTime startUtc, DateTime endUtc) { public DateTime StartUtc { get; } = startUtc; public DateTime EndUtc { get; set; } = endUtc; public bool Completed { get; set; } }
}
