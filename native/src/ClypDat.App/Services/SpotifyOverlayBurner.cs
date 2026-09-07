using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>Safely burns one rendered card into one clip.</summary>
internal static class SpotifyOverlayBurner
{
    public static async Task<bool> BurnAsync(string clipPath, string cardPath, string? position, CancellationToken token = default)
    {
        if (!FfmpegPathResolver.IsAvailable || !File.Exists(clipPath) || !File.Exists(cardPath)) return false;
        var folder = Path.Combine(Path.GetDirectoryName(clipPath)!, ".clypdat-overlay-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(folder, "burned" + Path.GetExtension(clipPath));
        try
        {
            Directory.CreateDirectory(folder);
            var graph = OverlayGraph(position);
            var success = await EncodeAsync(clipPath, cardPath, output, graph, HardwareCodecArguments(), token).ConfigureAwait(false);
            // Hardware failures must not discard a successfully saved clip.
            if (!success && ExportEncoderProbe.Family != "software")
                success = await EncodeAsync(clipPath, cardPath, output, graph, SoftwareCodecArguments(), token).ConfigureAwait(false);
            if (!success || !File.Exists(output) || new FileInfo(output).Length == 0) return false;

            // Same directory means same volume. Move with overwrite works for
            // formats where File.Replace is unsupported (notably FAT/exFAT).
            File.Move(output, clipPath, true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            AppLog.Error($"Spotify overlay: burning '{clipPath}' failed.", error);
            return false;
        }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } }
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
        args.Add("-loop"); args.Add("1"); args.Add("-i"); args.Add(card);
        args.Add("-filter_complex"); args.Add(graph);
        args.Add("-map"); args.Add("[video]"); args.Add("-map"); args.Add("0:a?"); args.Add("-map_metadata"); args.Add("0");
        foreach (var item in codec) args.Add(item);
        args.Add("-c:a"); args.Add("copy"); args.Add("-shortest"); args.Add("-movflags"); args.Add("+faststart"); args.Add(output);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(token);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false); await stdout.ConfigureAwait(false);
        if (process.ExitCode == 0) return true;
        if (!string.IsNullOrWhiteSpace(error)) AppLog.Error($"Spotify overlay: ffmpeg said '{error.Trim()}'.");
        return false;
    }

    private static string OverlayGraph(string? position)
    {
        const string margin = "main_h*0.035";
        var x = position?.EndsWith("Right", StringComparison.OrdinalIgnoreCase) == true ? "main_w-overlay_w-" + margin : margin;
        var y = position?.StartsWith("Top", StringComparison.OrdinalIgnoreCase) == true ? margin :
            position?.StartsWith("Center", StringComparison.OrdinalIgnoreCase) == true ? "(main_h-overlay_h)/2" : "main_h-overlay_h-" + margin;
        return $"[0:v:0][1:v:0]overlay={x}:{y}:eof_action=pass[video]";
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
