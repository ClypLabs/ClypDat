using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

internal interface ICameraPreviewService : IDisposable
{
    event Action<CameraPreviewFrame>? FrameReady;
    event Action<CameraPreviewFailure>? Failed;
    bool IsRunning { get; }
    int Session { get; }
    void Start(string deviceMoniker);
    void Stop();
}

internal sealed class CameraPreviewFrame : IDisposable
{
    private byte[]? _pixels;
    public CameraPreviewFrame(int session, byte[] pixels, long sequence = 0, long timestamp = 0) { Session = session; _pixels = pixels; Sequence = sequence; Timestamp = timestamp; }
    public int Session { get; }
    public long Sequence { get; }
    public long Timestamp { get; }
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CameraPreviewFrame));
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
    }
}

internal sealed record CameraPreviewFailure(int Session, string Message);
internal sealed record CameraPreviewMode(int Width, int Height, double FramesPerSecond, string Format, bool IsCompressed)
{
    public override string ToString() => $"{Width}x{Height} {Format} {FramesPerSecond:0.###} FPS";
}

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
    public event Action<CameraPreviewFailure>? Failed;
    public bool IsRunning { get { lock (_gate) return _process is not null; } }
    public int Session { get { lock (_gate) return _generation; } }

    public void Start(string deviceMoniker)
    {
        Stop();
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        CancellationTokenSource cancellation;
        int generation;
        lock (_gate) { cancellation = _cancellation = new CancellationTokenSource(); generation = ++_generation; }
        if (!File.Exists(ffmpeg)) { _ = Task.Run(() => Fail(generation, "FFmpeg is unavailable.")); return; }
        _ = StartAsync(ffmpeg, deviceMoniker, generation, cancellation.Token);
    }

    private async Task StartAsync(string ffmpeg, string deviceMoniker, int generation, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        IReadOnlyList<CameraPreviewMode> modes;
        try { modes = await CameraPreviewModeProbe.ListAsync(ffmpeg, deviceMoniker, cancellationToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { AppLog.Debug($"Camera preview mode probe failed: {error.Message}"); modes = Array.Empty<CameraPreviewMode>(); }
        if (!IsCurrent(generation, null)) return;
        var candidates = modes.Count == 0 ? new CameraPreviewMode?[] { null } : modes.Cast<CameraPreviewMode?>().Append(null).ToArray();
        if (modes.Count == 0) AppLog.Debug("Camera preview mode probe unavailable; using device default.");
        foreach (var mode in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = TryStartProcess(ffmpeg, deviceMoniker, mode);
            if (process is null) continue;
            // DirectShow rejects unsupported advertised combinations after process
            // launch. Give that fast failure a chance before committing this mode.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            if (process.HasExited) { AppLog.Debug($"Camera preview mode rejected: {mode?.ToString() ?? "device default"}."); process.Dispose(); continue; }
            lock (_gate)
            {
                if (generation != _generation || _cancellation?.IsCancellationRequested != false) { process.Dispose(); return; }
                _process = process;
            }
            AppLog.Debug($"Camera preview started in {started.Elapsed.TotalSeconds:0.000}s: {(mode?.ToString() ?? "device default")}.");
            _ = DrainStderrAsync(process, cancellationToken);
            _ = ReadFramesAsync(process, generation, cancellationToken);
            return;
        }
        Fail(generation, "Camera preview could not start.");
    }

    private Process? TryStartProcess(string ffmpeg, string deviceMoniker, CameraPreviewMode? mode)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        foreach (var argument in BuildArguments(deviceMoniker, mode)) info.ArgumentList.Add(argument);
        try { return Process.Start(info); }
        catch (Exception error) { AppLog.Debug($"Camera preview mode rejected ({mode?.ToString() ?? "device default"}): {error.Message}"); return null; }
    }

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

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try { var text = await process.StandardError.ReadToEndAsync(cancellationToken); if (!string.IsNullOrWhiteSpace(text)) AppLog.Debug($"Camera preview FFmpeg: {text.Trim()}"); }
        catch (OperationCanceledException) { }
    }

    private async Task ReadFramesAsync(Process process, int generation, CancellationToken cancellationToken)
    {
        byte[]? pixels = null; var offset = 0; var received = false; long sequence = 0, receivedFrames = 0, deliveredFrames = 0;
        Stopwatch? cadence = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                pixels ??= ArrayPool<byte>.Shared.Rent(FrameBytes);
                var readTask = process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(offset, FrameBytes - offset), cancellationToken).AsTask();
                if (!received && await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)) != readTask) throw new TimeoutException("Camera preview timed out waiting for frames.");
                var read = await readTask;
                if (read == 0) throw new InvalidOperationException("Camera preview stopped before a frame arrived.");
                offset += read;
                if (offset != FrameBytes) continue;
                received = true; offset = 0; receivedFrames++; cadence ??= Stopwatch.StartNew();
                if (!IsCurrent(generation, process)) return;
                var frame = new CameraPreviewFrame(generation, pixels, ++sequence, Stopwatch.GetTimestamp()); pixels = null;
                var handler = FrameReady;
                if (handler is null) frame.Dispose(); else { handler(frame); deliveredFrames++; }
                if (cadence.Elapsed < TimeSpan.FromSeconds(5)) continue;
                AppLog.Debug($"Camera preview cadence: received={receivedFrames}, delivered={deliveredFrames}, seconds={cadence.Elapsed.TotalSeconds:0.0}.");
                receivedFrames = 0; deliveredFrames = 0; cadence.Restart();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error("Camera preview failed", error);
            Fail(generation, error is TimeoutException ? "Camera timed out. Check it is not in use." : "Camera preview failed. Check access or camera use.");
        }
        finally { if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels); }
    }

    private bool IsCurrent(int generation, Process? process) { lock (_gate) return generation == _generation && (process is null || _process == process); }
    private void Fail(int generation, string message)
    {
        lock (_gate) { if (generation != _generation) return; }
        Failed?.Invoke(new CameraPreviewFailure(generation, message));
        Stop();
    }

    public void Stop()
    {
        Process? process; CancellationTokenSource? cancellation;
        lock (_gate) { ++_generation; process = _process; cancellation = _cancellation; _process = null; _cancellation = null; }
        cancellation?.Cancel();
        if (process is not null) { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); }
        cancellation?.Dispose();
    }
    public void Dispose() => Stop();
}

internal static class CameraPreviewModeProbe
{
    private static readonly Regex Option = new(@"(?:(?<kind>pixel_format|vcodec)=(?<format>[^\s]+)).*?(?:min\s+s=(?<minWidth>\d+)x(?<minHeight>\d+)\s+fps=(?<minFps>[\d.]+))?.*?max\s+s=(?<width>\d+)x(?<height>\d+)\s+fps=(?<fps>[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<IReadOnlyList<CameraPreviewMode>> ListAsync(string ffmpeg, string deviceMoniker, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-list_options", "true", "-f", "dshow", "-i", $"video={deviceMoniker}" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not start mode probe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return Parse(output);
    }

    internal static IReadOnlyList<CameraPreviewMode> Parse(string output)
    {
        var modes = Option.Matches(output).Select(match => new CameraPreviewMode(
            int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["fps"].Value, CultureInfo.InvariantCulture), match.Groups["format"].Value,
            match.Groups["kind"].Value.Equals("vcodec", StringComparison.OrdinalIgnoreCase)))
            .Where(mode => mode.FramesPerSecond > 0).Distinct().ToList();
        return modes.OrderByDescending(mode => RateBand(mode.FramesPerSecond)).ThenByDescending(mode => mode.FramesPerSecond)
            .ThenBy(mode => CoversPreview(mode) ? 0 : 1).ThenBy(mode => mode.Width * mode.Height)
            .ThenBy(mode => mode.Format.Equals("nv12", StringComparison.OrdinalIgnoreCase) ? 0 : mode.IsCompressed ? 2 : 1).ToArray();
    }
    private static int RateBand(double rate) => rate >= 59 ? 3 : rate >= 29 ? 2 : 1;
    private static bool CoversPreview(CameraPreviewMode mode) => mode.Width >= CameraPreviewService.Width && mode.Height >= CameraPreviewService.Height;
}
