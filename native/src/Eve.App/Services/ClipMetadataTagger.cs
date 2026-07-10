using System.Diagnostics;

namespace Eve.App.Services;

public static class ClipMetadataTagger
{
    public const string BackendTagKey = "EVE_CAPTURE_BACKEND";

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
            foreach (var arg in new[] { "-y", "-v", "error", "-i", path, "-map", "0", "-c", "copy", "-metadata", $"{BackendTagKey}={backendLabel}", taggedPath })
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
