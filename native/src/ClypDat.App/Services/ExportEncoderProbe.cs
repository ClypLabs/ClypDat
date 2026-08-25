using System.Diagnostics;

namespace ClypDat.App.Services;

/// <summary>
/// Works out which vendor's hardware encoder this machine can actually use for
/// CLI exports, once per run.
///
/// Export used to ask for NVENC unconditionally and treat any ffmpeg failure as
/// "no hardware encoder, redo it on the CPU". On an AMD or Intel machine that
/// meant every export paid a failed NVENC attempt and then encoded on libx264 -
/// slow, and needlessly so, since those machines have a perfectly good encoder
/// under a different name. Capture already picks per vendor (see
/// NativeReplayBuffer.EncoderCandidates); this is the same idea for the code
/// paths that shell out to ffmpeg.exe instead of driving libavcodec directly.
///
/// Presence in the ffmpeg build proves nothing - h264_nvenc is compiled into
/// every build regardless of what GPU is installed, and only fails when it is
/// opened. So this actually runs a tiny encode and keeps whichever family
/// survives it.
/// </summary>
public static class ExportEncoderProbe
{
    /// <summary>
    /// Vendor suffix that worked ("nvenc"/"amf"/"qsv"), or null if none did and
    /// exports should go straight to the CPU encoder. Blocks on the first call if
    /// Prewarm's run has not finished yet.
    /// </summary>
    public static string? Family => UiPreviewMode.Enabled ? null : _probe.Value;

    /// <summary>
    /// Vendor suffix whose AV1 encoder passed an actual FFmpeg encode, or null
    /// when this machine cannot hardware-encode AV1. Blocks until prewarm ends.
    /// </summary>
    public static string? Av1Family => UiPreviewMode.Enabled ? null : _av1Probe.Value;
    public static bool Av1ProbeCompleted => UiPreviewMode.Enabled || _av1Probe.IsValueCreated;

    // Same order capture uses, so a machine that somehow has two usable encoders
    // gets the same answer from both halves of the app.
    private static readonly string[] Candidates = { "nvenc", "amf", "qsv" };

    private static readonly Lazy<string?> _probe = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string?> _av1Probe = new(DetectAv1, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Runs the probe ahead of the first export so the export itself never waits
    /// on it. Safe to call more than once - Lazy runs the detection a single time.
    /// </summary>
    public static void Prewarm()
    {
        if (UiPreviewMode.Enabled) return;
        Task.Run(() => _ = _probe.Value);
        Task.Run(() => _ = _av1Probe.Value);
    }

    private static string? Detect()
    {
        foreach (var family in Candidates)
        {
            if (CanEncode($"h264_{family}"))
            {
                AppLog.Info($"Export: using {family} hardware encoder.");
                return family;
            }
        }

        AppLog.Info("Export: no hardware encoder available, exports will use the CPU encoder.");
        return null;
    }

    private static string? DetectAv1()
    {
        foreach (var family in Candidates)
        {
            if (CanEncode($"av1_{family}"))
            {
                AppLog.Info($"Replay: using {family} hardware AV1 encoder when Auto is selected.");
                return family;
            }
        }

        AppLog.Info("Replay: no hardware AV1 encoder available; Auto will use H.264.");
        return null;
    }

    // Encodes a handful of frames of generated colour to nowhere. Cheap enough to
    // run at startup, and unlike "-encoders" it fails exactly when a real encode
    // would. Best-effort throughout: anything unexpected here means "assume this
    // family does not work" and move to the next, never take export down.
    //
    // 320x240 rather than something smaller because NVENC enforces a minimum frame
    // size and rejects anything under it with "Frame Dimension less than the
    // minimum supported value" - at 128x128 this probe failed on a machine with a
    // perfectly working NVIDIA encoder, which would have quietly put every NVIDIA
    // user on the CPU encoder. Any probe resolution change needs re-checking
    // against all three vendors for the same reason.
    private static bool CanEncode(string encoder)
    {
        try
        {
            var startInfo = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-f", "lavfi", "-i", "color=black:s=320x240:r=30:d=0.2",
                         "-c:v", encoder,
                         "-f", "null", "-"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            // Draining both pipes matters even though the output is discarded: a
            // process that fills a redirected pipe blocks forever instead of
            // exiting, which would hang the probe rather than fail it.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception error)
        {
            AppLog.Info($"Export: probe for {encoder} failed ({error.Message}).");
            return false;
        }
    }
}
