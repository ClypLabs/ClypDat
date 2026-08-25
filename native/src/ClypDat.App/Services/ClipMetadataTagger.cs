using System.Diagnostics;

namespace ClypDat.App.Services;

public static class ClipMetadataTagger
{
    // MP4/mov muxers only persist a whitelisted set of format-level metadata keys
    // (title, comment, artist, etc.) and silently drop arbitrary custom keys -
    // confirmed by directly probing a tagged file and finding the custom key gone.
    // "comment" is one of the recognized keys, so the backend name is embedded
    // inside it instead, prefixed for unambiguous parsing on read-back.
    // New clips use CLYPDAT_CAPTURE_BACKEND. MediaProbeService also accepts
    // LegacyBackendTagKey so existing EVE-era clips keep their label.
    public const string BackendTagKey = "CLYPDAT_CAPTURE_BACKEND";
    internal const string LegacyBackendTagKey = "EVE_CAPTURE_BACKEND";
    private const string CommentKey = "comment";

    public static string BuildCommentValue(string backendLabel) => $"{BackendTagKey}={backendLabel}";

    // Legacy clips (recorded before the EVE -> ClypDat rebrand) have "EVE Native"
    // baked into their file's metadata verbatim - stripping a leading app-name
    // prefix here at display time normalizes those down to the bare "Native"/
    // "Native Full Session" tag every clip (old or new) actually carries,
    // without needing to rewrite/re-mux every existing clip's file. That bare
    // tag then gets its own display swap below: "Native" is ClypDat's own
    // built-in capture backend (unlike Windows Capture), so it reads as "Captured with: ClypDat" rather
    // than exposing the internal backend name.
    public static string NormalizeBackendLabel(string backendLabel)
    {
        foreach (var prefix in new[] { "EVE ", "ClypDat " })
        {
            if (backendLabel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                backendLabel = backendLabel[prefix.Length..];
                break;
            }
        }

        if (string.Equals(backendLabel, "Native", StringComparison.OrdinalIgnoreCase)) return "ClypDat";
        if (string.Equals(backendLabel, "Native Full Session", StringComparison.OrdinalIgnoreCase)) return "ClypDat Full Session";

        return backendLabel;
    }

    public static async Task<string> TagCaptureBackendAsync(string path, string backendLabel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return path;
        var taggedPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(path)}.tag{Path.GetExtension(path)}");
        try
        {
            var startInfo = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
            };
            foreach (var arg in new[] { "-y", "-v", "error", "-i", path, "-map", "0", "-c", "copy", "-metadata", $"{CommentKey}={BuildCommentValue(backendLabel)}", taggedPath })
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(taggedPath) || new FileInfo(taggedPath).Length == 0)
            {
                TryDelete(taggedPath);
                AppLog.Info($"Clip backend tag failed, keeping untagged file: path={path}, backend={backendLabel}.");
                return path;
            }

            File.Delete(path);
            File.Move(taggedPath, path);
            return path;
        }
        catch (Exception error)
        {
            AppLog.Error("Clip backend tag failed", error);
            TryDelete(taggedPath);
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
