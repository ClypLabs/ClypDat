using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClypDat.App.Services;

internal sealed record SpotifyRenderSpec(SpotifyTimeline? Timeline, SpotifyCard? LegacyCard, int Width, int Height,
    double Start, double Duration, double Speed, string Position, FontFamily Font, bool DynamicBackground = true)
{
    public (SpotifyCard? Card, double SongSeconds) At(double outputSeconds)
    {
        if (Timeline is null) return (LegacyCard, Math.Max(0, outputSeconds));
        var sourceTime = Start + outputSeconds * Speed;
        var sample = SpotifyTimelineSidecar.At(Timeline, sourceTime);
        if (sample is null || string.IsNullOrWhiteSpace(sample.Track)) return (null, 0);
        var start = sample.OffsetSeconds;
        foreach (var prior in Timeline.Samples.Where(item => item.OffsetSeconds <= sample.OffsetSeconds).Reverse())
        {
            if (!prior.Available || prior.TrackId != sample.TrackId || prior.Track != sample.Track) break;
            start = prior.OffsetSeconds;
        }
        return (new(sample.Track, sample.Artist, sample.Album,
            sample.DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null,
            sample.ProgressMs is { } p ? TimeSpan.FromMilliseconds(p) : null, sample.ArtPath), Math.Max(0, sourceTime - start));
    }
}

public sealed class SpotifyOverlayAnimation : IDisposable
{
    public string Path { get; }
    public string Position { get; }
    private SpotifyOverlayAnimation(string path, string position) { Path = path; Position = position; }
    public void Dispose() { try { File.Delete(Path); } catch { } }
    internal static async Task<SpotifyOverlayAnimation> PrepareAsync(SpotifyRenderSpec spec, CancellationToken token)
    {
        var path = System.IO.Path.ChangeExtension(SpotifyOverlayCardRenderer.WorkPath("animation"), ".mkv");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        SpotifyCardFrames? renderer = null;
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } };
        try
        {
            renderer = await Dispatcher.UIThread.InvokeAsync(() => new SpotifyCardFrames(spec.Width, spec.Height, spec.Position, spec.Font, spec.DynamicBackground));
            foreach (var arg in new[] { "-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{renderer.Width}x{renderer.Height}",
                "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", path }) process.StartInfo.ArgumentList.Add(arg);
            token.ThrowIfCancellationRequested();
            process.Start();
            var errors = process.StandardError.ReadToEndAsync();
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            try
            {
                for (var frame = 0; frame < Math.Ceiling(spec.Duration * 30); frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var state = spec.At(frame / 30.0);
                    await Dispatcher.UIThread.InvokeAsync(() => { renderer.Render(state.Card, state.SongSeconds); renderer.CopyStraightPixels(); }, DispatcherPriority.Background);
                    await process.StandardInput.BaseStream.WriteAsync(renderer.Pixels, token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                var error = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException(error);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await errors.ConfigureAwait(false);
            }
            return new(path, spec.Position);
        }
        catch { try { File.Delete(path); } catch { } token.ThrowIfCancellationRequested(); throw; }
        finally { if (renderer is not null) await Dispatcher.UIThread.InvokeAsync(renderer.Dispose); }
    }
}

internal sealed class SpotifyAnimationCache(SpotifyRenderSpec? spec) : IDisposable
{
    private readonly Dictionary<(int, int), SpotifyOverlayAnimation> _animations = new();
    public SpotifyRenderSpec? Spec { get; } = spec;
    public async Task<SpotifyOverlayAnimation?> GetAsync(int width, int height, CancellationToken token)
    {
        if (Spec is null) return null;
        if (_animations.TryGetValue((width, height), out var animation)) return animation;
        animation = await SpotifyOverlayAnimation.PrepareAsync(Spec with { Width = width, Height = height }, token);
        _animations.Add((width, height), animation);
        return animation;
    }
    public void Dispose() { foreach (var animation in _animations.Values) animation.Dispose(); }
}
