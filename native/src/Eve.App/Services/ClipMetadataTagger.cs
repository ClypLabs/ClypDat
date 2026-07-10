using System.Diagnostics;

namespace Eve.App.Services;

public static class ClipMetadataTagger
{
    // MP4/mov muxers only persist a whitelisted set of format-level metadata keys
    // (title, comment, artist, etc.) and silently drop arbitrary custom keys -
    // confirmed by directly probing a tagged file and finding the custom key gone.
    // "comment" is one of the recognized keys, so the backend name is embedded
    // inside it instead, prefixed for unambiguous parsing on read-back.
    public const string BackendTagKey = "EVE_CAPTURE_BACKEND";
    private const string CommentKey = "comment";

    public static string BuildCommentValue(string backendLabel) => $"{BackendTagKey}={backendLabel}";

    public static async Task<string> TagCaptureBackendAsync(string path, string backendLabel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return path;
        var taggedPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(path)}.tag{Path.GetExtension(path)}");
        try
        {
            var startInfo = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
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
