using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace ClypDat.App.Services;

/// <summary>Safely burns one rendered card into one clip.</summary>
internal enum SpotifyOverlayOutcome { Skipped, Completed, Failed, Cancelled }

internal static class SpotifyOverlayBurner
{
    public const string BurnMarker = "CLYPDAT_SPOTIFY_OVERLAY";

    public static Task<SpotifyOverlayOutcome> BurnAsync(string clipPath, SpotifyOverlayAnimation animation, CancellationToken token = default, IReadOnlyList<string>? preferredCodec = null) =>
        BurnAsync(clipPath, animation.Path, animation.Position, token, preferredCodec, animation.Bounds);

    public static async Task<SpotifyOverlayOutcome> BurnAsync(string clipPath, string cardPath, string? position, CancellationToken token = default, IReadOnlyList<string>? preferredCodec = null, SpotifyOverlayBounds? bounds = null)
    {
        if (!FfmpegPathResolver.IsAvailable || !File.Exists(clipPath) || !File.Exists(cardPath)) return SpotifyOverlayOutcome.Failed;
        var folder = Path.Combine(Path.GetDirectoryName(clipPath)!, ".clypdat-overlay-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(folder, "burned" + Path.GetExtension(clipPath));
        var backup = Path.Combine(folder, "original" + Path.GetExtension(clipPath));
        var keepBackup = false;
        try
        {
            var original = await InspectAsync(clipPath, token).ConfigureAwait(false);
            if (original.Burned) return SpotifyOverlayOutcome.Skipped;
            var created = File.GetCreationTimeUtc(clipPath);
            Directory.CreateDirectory(folder);
            var graph = ClipRenderFilters.ComposeWithAnimation(null, position, "[0:v:0]", "[video]", bounds);
            var success = await EncodeAsync(clipPath, cardPath, output, graph, preferredCodec ?? HardwareCodecArguments(), token).ConfigureAwait(false);
            if (!success && (preferredCodec is not null || ExportEncoderProbe.Family is not null))
                success = await EncodeAsync(clipPath, cardPath, output, graph, SoftwareCodecArguments(), token).ConfigureAwait(false);
            if (!success) return SpotifyOverlayOutcome.Failed;
            var encoded = await InspectAsync(output, token).ConfigureAwait(false);
            if (!IsValidReplacement(original, encoded)) throw new InvalidDataException("Spotify overlay output did not preserve duration, audio, or metadata.");
            File.SetCreationTimeUtc(output, created);
            await ReplaceAsync(output, clipPath, backup, token).ConfigureAwait(false);
            // Replacement is the commit point. Finish validation even if cancelled now.
            keepBackup = true;
            var installed = await InspectAsync(clipPath, CancellationToken.None).ConfigureAwait(false);
            if (!IsValidReplacement(original, installed))
            {
                await ReplaceAsync(backup, clipPath, Path.Combine(folder, "invalid.mp4"), CancellationToken.None).ConfigureAwait(false);
                keepBackup = false;
                return SpotifyOverlayOutcome.Failed;
            }
            File.SetCreationTimeUtc(clipPath, created);
            keepBackup = false;
            return SpotifyOverlayOutcome.Completed;
        }
        catch (OperationCanceledException) { return SpotifyOverlayOutcome.Cancelled; }
        catch (Exception error)
        {
            if (keepBackup && File.Exists(backup))
            {
                try
                {
                    await ReplaceAsync(backup, clipPath, Path.Combine(folder, "invalid.mp4"), CancellationToken.None).ConfigureAwait(false);
                    keepBackup = false;
                }
                catch (Exception rollbackError) { AppLog.Error($"Spotify overlay rollback retained at '{backup}'.", rollbackError); }
            }
            AppLog.Error($"Spotify overlay: burning '{clipPath}' failed. Rollback copy: '{backup}'.", error);
            return SpotifyOverlayOutcome.Failed;
        }
        finally
        {
            try
            {
                if (keepBackup) { if (File.Exists(output)) File.Delete(output); }
                else if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch { }
        }
    }

    internal static async Task ReplaceAsync(string output, string clip, string backup, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { File.Replace(output, clip, backup); return; }
            catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
            { await Task.Delay(Math.Min(250 * (1 << attempt), 2000), token).ConfigureAwait(false); }
        }
    }

    internal sealed record Inspection(double Duration, string[] Audio, bool Burned, Dictionary<string, string> Tags, bool HasVideo);

    internal static bool IsValidReplacement(Inspection original, Inspection encoded) =>
        encoded.HasVideo && encoded.Burned && original.Duration > 0 &&
        Math.Abs(original.Duration - encoded.Duration) <= 0.15 &&
        original.Audio.SequenceEqual(encoded.Audio) &&
        original.Tags.Where(tag => !new[] { "encoder", "major_brand", "minor_version", "compatible_brands" }.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
            .All(tag => encoded.Tags.TryGetValue(tag.Key, out var value) && value == tag.Value);

    internal static async Task<Inspection> InspectAsync(string path, CancellationToken token = default)
    {
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfprobePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { await KillAndWaitAsync(process).ConfigureAwait(false); await Task.WhenAll(stdout, stderr); throw; }
        if (process.ExitCode != 0) throw new InvalidDataException(await stderr);
        using var json = JsonDocument.Parse(await stdout);
        var format = json.RootElement.GetProperty("format");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("tags", out var values))
            foreach (var tag in values.EnumerateObject()) tags[tag.Name] = tag.Value.GetString() ?? "";
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio")
            .Select(s => string.Join(":", new[] { "codec_name", "sample_rate", "channels" }.Select(k => s.TryGetProperty(k, out var v) ? v.ToString() : ""))).ToArray();
        return new(double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture), audio,
            tags.TryGetValue(BurnMarker, out var marker) && marker == "1", tags,
            streams.Any(s => s.GetProperty("codec_type").GetString() == "video"));
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static async Task<bool> EncodeAsync(string clip, string card, string output, string graph, IReadOnlyList<string> codec, CancellationToken token)
    {
        if (File.Exists(output)) File.Delete(output);
        using var process = new Process { StartInfo = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = FfmpegPathResolver.WorkingDirectory
        }};
        var args = process.StartInfo.ArgumentList;
        args.Add("-v"); args.Add("error"); args.Add("-y");
        args.Add("-i"); args.Add(clip);
        if (Path.GetExtension(card).Equals(".png", StringComparison.OrdinalIgnoreCase)) { args.Add("-loop"); args.Add("1"); }
        args.Add("-i"); args.Add(card);
        args.Add("-filter_complex"); args.Add(graph);
        args.Add("-map"); args.Add("[video]"); args.Add("-map"); args.Add("0:a?"); args.Add("-map_metadata"); args.Add("0");
        foreach (var item in codec) args.Add(item);
        args.Add("-c:a"); args.Add("copy"); args.Add("-shortest"); args.Add("-movflags"); args.Add("+faststart+use_metadata_tags"); args.Add("-metadata"); args.Add(BurnMarker + "=1"); args.Add(output);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { await KillAndWaitAsync(process).ConfigureAwait(false); await Task.WhenAll(stderr, stdout); throw; }
        var error = await stderr.ConfigureAwait(false); await stdout.ConfigureAwait(false);
        if (process.ExitCode == 0) return true;
        if (!string.IsNullOrWhiteSpace(error)) AppLog.Error($"Spotify overlay: ffmpeg said '{error.Trim()}'.");
        return false;
    }

    private static IReadOnlyList<string> HardwareCodecArguments() => ExportEncoderProbe.Family switch
    {
        "nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20", "-b:v", "0" },
        "amf" => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" },
        "qsv" => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20" },
        _ => SoftwareCodecArguments()
    };

    private static IReadOnlyList<string> SoftwareCodecArguments() => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20" };
}
