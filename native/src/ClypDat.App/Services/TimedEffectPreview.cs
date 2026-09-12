using System.Diagnostics;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal static class TimedEffectPreview
{
    private const long Limit = 1024L * 1024 * 1024;
    public static string WorkPath()
    {
        var root = Path.Combine(AppDataPaths.Root, "effect-preview");
        Directory.CreateDirectory(root);
        foreach (var file in new DirectoryInfo(root).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            try { file.Delete(); } catch (IOException) { }
        if (new DirectoryInfo(root).GetFiles().Sum(f => f.Length) > Limit || new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < Limit)
            throw new IOException("Effect preview needs 1 GB free space. Remove old previews or free disk space.");
        return Path.Combine(root, $"{Guid.NewGuid():N}.mp4");
    }

    public static async Task RenderAsync(IReadOnlyList<string> arguments, string output, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        var existingBytes = new DirectoryInfo(Path.GetDirectoryName(output)!).GetFiles().Sum(f => f.Length);
        var available = Limit - existingBytes - 1024 * 1024;
        if (available < 1024 * 1024) throw new IOException("Preview cache is full. Close the editor to release its previous video.");
        process.StartInfo.ArgumentList.Add("-fs"); process.StartInfo.ArgumentList.Add(available.ToString());
        process.StartInfo.ArgumentList.Add(output);
        try
        {
            process.Start();
            using var cancel = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync(token);
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await stdout;
            var diagnostic = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Cannot render text/blur preview. FFmpeg: {diagnostic[^Math.Min(1500, diagnostic.Length)..]}");
            if (new FileInfo(output).Length >= available - 1024 * 1024) throw new IOException("Effect preview exceeds 1 GB. Use a shorter clip.");
        }
        catch { AudioCapturePipeline.TryDelete(output); throw; }
    }
}
