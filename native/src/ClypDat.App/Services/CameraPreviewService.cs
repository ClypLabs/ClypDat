using System.Buffers;
using System.Globalization;

namespace ClypDat.App.Services;

/// <summary>Settings preview control adapter. Native capture owns the device,
/// process, complete-frame buffers, mode selection, and bounded teardown.</summary>
internal sealed class CameraPreviewService : ICameraPreviewService
{
    internal const int Width = 640;
    internal const int Height = 360;
    internal const int FrameBytes = Width * Height * 4;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _task = Task.CompletedTask;
    private int _generation;
    private bool _running;
    public event Action<CameraPreviewFrame>? FrameReady;
    public event Action<CameraPreviewFailure>? Failed;
    public bool IsRunning { get { lock (_gate) return _running; } }
    public int Session { get { lock (_gate) return _generation; } }

    public void Start(string deviceMoniker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceMoniker);
        lock (_gate)
        {
            _cancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var generation = ++_generation;
            var previous = _task;
            _running = true;
            _task = Task.Run(() => RunAsync(previous, deviceMoniker, generation, cancellation));
        }
    }
    private async Task RunAsync(Task previous, string deviceMoniker, int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            using var preview = NativeCameraPreview.Create();
            preview.Start(deviceMoniker);
            ulong lastSequence = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var pixels = ArrayPool<byte>.Shared.Rent(FrameBytes);
                CameraPreviewFrame? frame = null;
                try
                {
                    var info = preview.Copy(pixels);
                    if (info.RequiredBytes > 0 && info.Sequence != lastSequence)
                    {
                        if (info.Width != Width || info.Height != Height || info.Stride != Width * 4 || info.RequiredBytes != FrameBytes)
                            throw new InvalidOperationException("Native camera preview returned an incompatible frame.");
                        lastSequence = info.Sequence;
                        frame = new(generation, pixels, checked((long)info.Sequence), info.TimestampQpc);
                        pixels = null!;
                        Action<CameraPreviewFrame>? handler;
                        lock (_gate) handler = generation == _generation && !cancellation.IsCancellationRequested ? FrameReady : null;
                        if (handler is not null) { handler(frame); frame = null; }
                    }
                    if (info.Running == 0)
                        throw new InvalidOperationException(preview.Error() is { Length: > 0 } error ? error : "Camera preview stopped.");
                }
                finally
                {
                    frame?.Dispose();
                    if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
                }
                await Task.Delay(8, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            Action<CameraPreviewFailure>? handler;
            lock (_gate)
            {
                handler = generation == _generation && !cancellation.IsCancellationRequested ? Failed : null;
                if (generation == _generation) _running = false;
            }
            AppLog.Error("Native camera preview failed", error);
            handler?.Invoke(new(generation, error.Message));
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation) { _running = false; _cancellation = null; }
            }
            cancellation.Dispose();
        }
    }
    // Settings tooling contract; production commands execute in RecorderCore.
    internal static IReadOnlyList<string> BuildArguments(string deviceMoniker, CameraPreviewMode? mode)
    {
        var arguments = new List<string> { "-hide_banner", "-f", "dshow" };
        if (mode is not null)
        {
            arguments.AddRange(["-video_size", $"{mode.Width}x{mode.Height}", "-framerate", mode.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)]);
            arguments.Add(mode.IsCompressed ? "-vcodec" : "-pixel_format"); arguments.Add(mode.Format);
        }
        arguments.AddRange(["-i", $"video={deviceMoniker}", "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
        return arguments;
    }
    public void Stop()
    {
        lock (_gate) { ++_generation; _running = false; _cancellation?.Cancel(); _cancellation = null; }
    }
    public void Dispose() => Stop();
}
