using System.Diagnostics;
using System.Text;

namespace ClypDat.App.Services;

internal interface ICameraPreviewService : IDisposable
{
    event Action<CameraPreviewFrame>? FrameReady;
    event Action<string>? Failed;
    bool IsRunning { get; }
    int Session { get; }
    void Start(string deviceMoniker);
    void Stop();
}

internal sealed record CameraPreviewFrame(int Session, byte[] Pixels);

// This is deliberately independent from replay capture. FFmpeg owns only a video
// DirectShow stream and is killed when this short-lived settings preview ends.
internal sealed class CameraPreviewService : ICameraPreviewService
{
    internal const int Width = 640;
    internal const int Height = 360;
    internal const int FrameBytes = Width * Height * 4;
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public event Action<CameraPreviewFrame>? FrameReady;
    public event Action<string>? Failed;
    public bool IsRunning { get { lock (_gate) return _process is not null; } }
    public int Session { get { lock (_gate) return _generation; } }

    public void Start(string deviceMoniker)
    {
        Stop();
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { Failed?.Invoke("FFmpeg is unavailable."); return; }
        var info = new ProcessStartInfo(ffmpeg) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true
        };
        // ArgumentList preserves Unicode device names. Do not add an audio input.
        foreach (var argument in new[] { "-hide_banner", "-f", "dshow", "-i", $"video={deviceMoniker}", "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-r", "15", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception error) { AppLog.Error("Camera preview could not start", error); Failed?.Invoke("Camera preview could not start."); return; }
        if (process is null) { Failed?.Invoke("Camera preview could not start."); return; }
        var cancellation = new CancellationTokenSource();
        int generation;
        lock (_gate) { _process = process; _cancellation = cancellation; generation = ++_generation; }
        _ = DrainStderrAsync(process, cancellation.Token);
        _ = ReadFramesAsync(process, generation, cancellation.Token);
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try { var text = await process.StandardError.ReadToEndAsync(cancellationToken); if (!string.IsNullOrWhiteSpace(text)) AppLog.Debug($"Camera preview FFmpeg: {text.Trim()}"); }
        catch (OperationCanceledException) { }
    }

    private async Task ReadFramesAsync(Process process, int generation, CancellationToken cancellationToken)
    {
        var frame = new byte[FrameBytes]; var offset = 0; var received = false;
        var cadence = Stopwatch.StartNew(); long receivedFrames = 0, deliveredFrames = 0;
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var readTask = process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(offset, FrameBytes - offset), cancellationToken).AsTask();
                if (!received && await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)) != readTask) throw new TimeoutException("Camera preview timed out waiting for frames.");
                var read = await readTask;
                if (read == 0) throw new InvalidOperationException("Camera preview stopped before a frame arrived.");
                offset += read;
                if (offset != FrameBytes) continue;
                received = true; offset = 0; receivedFrames++;
                lock (_gate) { if (generation != _generation || _process != process) return; }
                FrameReady?.Invoke(new CameraPreviewFrame(generation, frame)); deliveredFrames++; frame = new byte[FrameBytes];
                if (cadence.Elapsed < TimeSpan.FromSeconds(5)) continue;
                AppLog.Debug($"Camera preview cadence: received={receivedFrames}, delivered={deliveredFrames}, seconds={cadence.Elapsed.TotalSeconds:0.0}.");
                receivedFrames = 0; deliveredFrames = 0; cadence.Restart();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) {
            AppLog.Error("Camera preview failed", error);
            lock (_gate) { if (generation != _generation) return; }
            Failed?.Invoke(error is TimeoutException ? "Camera timed out. Check it is not in use." : "Camera preview failed. Check access or camera use.");
            Stop();
        }
    }

    public void Stop()
    {
        Process? process; CancellationTokenSource? cancellation;
        lock (_gate) { ++_generation; process = _process; cancellation = _cancellation; _process = null; _cancellation = null; }
        cancellation?.Cancel(); cancellation?.Dispose();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        process.Dispose();
    }
    public void Dispose() => Stop();
}
