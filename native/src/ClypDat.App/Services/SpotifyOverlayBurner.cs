using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>
/// Writes the Spotify card into a saved clip, so the file itself carries it -
/// its thumbnail, its hover preview, and anywhere it is opened outside ClypDat.
///
/// Off by default, because it is not free: the clip is re-encoded once, which
/// costs a few seconds of encoder time immediately after a save and one
/// generation of quality, and the result is permanent. The export path draws
/// the same card without either cost, which is why that stays the default.
/// </summary>
internal static class SpotifyOverlayBurner
{
    /// <summary>
    /// Re-encodes <paramref name="clipPath"/> with <paramref name="videoFilter"/>
    /// applied and replaces it. Returns whether the file was rewritten.
    /// </summary>
    public static async Task<bool> BurnAsync(string clipPath, string videoFilter, CancellationToken token = default)
    {
        if (!FfmpegPathResolver.IsAvailable || string.IsNullOrWhiteSpace(videoFilter)) return false;
        if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath)) return false;

        // Beside the clip, not in %TEMP%: File.Replace cannot move across
        // volumes, and a library on D: with a temp folder on C: fails every
        // replace. Same reason ClipCorruptionRepairService works this way.
        var workFolder = Path.Combine(Path.GetDirectoryName(clipPath)!, ".clypdat-overlay-" + Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(workFolder, "burned" + Path.GetExtension(clipPath));

        try
        {
            Directory.CreateDirectory(workFolder);

            // Every audio track is copied through untouched - a clip carries
            // game, chat, microphone and music as separate streams, and this is
            // a video-only change. Re-encoding them would cost quality on four
            // tracks to draw text on one.
            var arguments = $"-v error -y -i \"{clipPath}\" -map 0:v:0 -map 0:a? -map_metadata 0 " +
                $"-vf \"{videoFilter}\" {string.Join(" ", CodecArguments())} -c:a copy -movflags +faststart \"{outputPath}\"";

            var exitCode = await RunAsync(FfmpegPathResolver.FfmpegPath, arguments, token).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                AppLog.Error($"Spotify overlay: burning into '{Path.GetFileName(clipPath)}' failed (ffmpeg exit {exitCode}).");
                return false;
            }

            // Replace, not delete-then-move: the original stays in place until
            // the replacement is proven, so a failure here leaves the clip the
            // user just captured exactly as it was.
            File.Replace(outputPath, clipPath, null);
            AppLog.Info($"Spotify overlay: burned into {Path.GetFileName(clipPath)}.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Spotify overlay: burning into '{clipPath}' failed.", error);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(workFolder)) Directory.Delete(workFolder, true); } catch { }
        }
    }

    // The same hardware-first choice the export path makes, minus the codec
    // menu: a burn re-encodes what the recorder produced, so it stays on H.264
    // rather than quietly changing the clip's codec behind the user.
    private static IReadOnlyList<string> CodecArguments() => ExportEncoderProbe.Family switch
    {
        "nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20", "-b:v", "0" },
        "amf" => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" },
        "qsv" => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20" },
        _ => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20" }
    };

    private static async Task<int> RunAsync(string executable, string arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = FfmpegPathResolver.WorkingDirectory
            },
            EnableRaisingEvents = true
        };

        process.Start();
        // Both pipes are drained: ffmpeg blocks once a pipe buffer fills, and a
        // burn that stalls forever is worse than one that fails.
        var error = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var errorText = await error.ConfigureAwait(false);
        await output.ConfigureAwait(false);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(errorText))
            AppLog.Error($"Spotify overlay: ffmpeg said '{errorText.Trim()}'.");

        return process.ExitCode;
    }
}
