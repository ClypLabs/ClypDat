using System.Diagnostics;
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
    private DateTime _cameraStartedUtc;
    private string? _cameraError;
    private bool _cameraReceivedFrames;

    public OverlayCaptureSession(string workRoot) => _workRoot = Path.Combine(workRoot, "overlays");

    public void Apply(OverlayCaptureSettings settings)
    {
        lock (_gate)
        {
            var changed = !Equals(_settings.Camera, settings.Camera);
            _settings = settings;
            if (changed) StopCameraUnderLock();
            if (_settings.Camera is not null && _camera is null) StartCameraUnderLock();
        }
    }

    public void Start()
    {
        lock (_gate)
            if (_settings.Camera is not null && _camera is null) StartCameraUnderLock();
    }

    public void Stop() { lock (_gate) StopCameraUnderLock(); }

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
                if (!TrySegmentWindow(file, out var segmentStart, out var segmentEnd) || segmentEnd <= startUtc || segmentStart >= endUtc) continue;
                var target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, true);
                assets.Add(new ClipOverlayAsset(Path.GetRelativePath(libraryRoot, target),
                    Math.Max(0, (segmentStart - startUtc).TotalSeconds), Math.Min((endUtc - startUtc).TotalSeconds, (segmentEnd - startUtc).TotalSeconds)));
            }
            return assets.Count == 0
                ? new ClipOverlayLayer(camera.FriendlyName, false, InitialTransform: _settings.CameraTransform.ToPresentationTransform(),
                    Error: "Camera frames have not reached a completed capture segment yet.")
                : new ClipOverlayLayer(camera.FriendlyName, true, Assets: assets, InitialTransform: _settings.CameraTransform.ToPresentationTransform());
        }
    }

    private bool TrySegmentWindow(string path, out DateTime start, out DateTime end)
    {
        start = end = DateTime.MinValue;
        // FFmpeg's segment muxer opens each file at its first keyframe. File
        // creation time is the only timestamp it exposes without a second
        // muxing pass, and shares the recorder's monotonic UTC clock.
        start = File.GetCreationTimeUtc(path);
        end = start + TimeSpan.FromSeconds(SegmentSeconds);
        return true;
    }

    private void StartCameraUnderLock()
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { _cameraError = "FFmpeg is unavailable."; return; }
        Directory.CreateDirectory(_workRoot);
        foreach (var file in Directory.EnumerateFiles(_workRoot, "*.mp4")) AudioCapturePipeline.TryDelete(file);
        var pattern = Path.Combine(_workRoot, "%d.mp4");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-f", "dshow", "-i", $"video={_settings.Camera!.DeviceMoniker}", "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-r", "15", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-bf", "0", "-sc_threshold", "0", "-f", "segment", "-segment_time", SegmentSeconds.ToString(), "-reset_timestamps", "1", "-segment_format_options", "movflags=+frag_keyframe+empty_moov+default_base_moof", pattern }) info.ArgumentList.Add(argument);
        try
        {
            _cameraStartedUtc = MonotonicClock.UtcNow;
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

    public void Dispose() { Stop(); try { if (Directory.Exists(_workRoot)) Directory.Delete(_workRoot, true); } catch { } }
}
