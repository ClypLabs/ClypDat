using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// Throwaway validation harness for Phase 1 of the native capture engine (see plan).
// Invoked via `ClypDat.exe --test-native-capture` (Program.cs) - not part of the normal
// app flow. Exercises the real NativeReplayBuffer class end-to-end: start, let the
// ring buffer accumulate for a few seconds, save, stop.
internal static class NativeReplayBufferSmokeTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    public static async Task RunAsync()
    {
        Console.WriteLine("NativeReplayBuffer smoke test starting...");

        var config = new ReplayBufferConfig(
            DurationSeconds: 30,
            MaxHeight: 1440,
            FrameRate: 120,
            CaptureX: 0,
            CaptureY: 0,
            CaptureWidth: 1920,
            CaptureHeight: 1080,
            ChatAudioDeviceName: string.Empty,
            ChatAudioDeviceId: string.Empty,
            ChatAudioProcessNames: Array.Empty<string>(),
            MicrophoneDeviceIds: Array.Empty<string>(),
            MicrophoneDeviceName: string.Empty,
            GameAudioExcludedProcesses: Array.Empty<string>(),
            GameDisplayName: "Native Capture Test",
            GameExecutableName: string.Empty,
            GameWindowTitle: string.Empty,
            GameWindowClass: string.Empty,
            FullSessionRecordingEnabled: true,
            FullSessionRecordingFolder: Path.Combine(Path.GetTempPath(), "clypdat-native-full-session-test"),
            CaptureCursor: true);

        var buffer = new NativeReplayBuffer(() => config);

        Console.WriteLine("Starting capture...");
        await buffer.StartAsync();
        Console.WriteLine($"Recording: {buffer.IsRecording}");

        GetCursorPos(out var originalCursor);
        using var cursorMotionCts = new CancellationTokenSource();
        var cursorMotion = MoveCursorAsync(originalCursor, cursorMotionCts.Token);
        Console.WriteLine("Letting ring buffer accumulate for 8 seconds with deterministic cursor motion...");
        for (var i = 0; i < 8; i++)
        {
            Console.WriteLine($"...tick {i + 1}/8 at {DateTime.Now:HH:mm:ss.fff}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        try
        {
            var outputFolder = Path.Combine(Path.GetTempPath(), "clypdat-native-capture-test");
            Directory.CreateDirectory(outputFolder);

            Console.WriteLine("Saving replay...");
            var outputPath = await buffer.SaveReplayAsync(outputFolder);
            Console.WriteLine($"Saved: {outputPath}");

            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"File size: {fileInfo.Length} bytes");
            await VerifyMotionAsync(outputPath);
        }
        catch (Exception error)
        {
            Console.WriteLine($"Save failed: {error}");
            throw;
        }

        finally
        {
            cursorMotionCts.Cancel();
            try { await cursorMotion; } catch (OperationCanceledException) { }
            SetCursorPos(originalCursor.X, originalCursor.Y);
            Console.WriteLine("Stopping capture...");
            await buffer.StopAsync();
            buffer.Dispose();
        }

        Console.WriteLine("Smoke test complete.");
    }

    private static async Task MoveCursorAsync(NativePoint origin, CancellationToken cancellationToken)
    {
        var step = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var horizontal = (step % 120) - 60;
            var vertical = ((step / 3) % 80) - 40;
            SetCursorPos(Math.Max(1, origin.X + horizontal), Math.Max(1, origin.Y + vertical));
            step++;
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task VerifyMotionAsync(string outputPath)
    {
        var result = await AudioCapturePipeline.RunProcessAsync("ffmpeg", new[]
        {
            "-v", "error", "-t", "6", "-i", outputPath, "-map", "0:v:0",
            "-vf", "fps=10,scale=64:-2", "-f", "framemd5", "-"
        }, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Motion validation failed: {result.Error.Trim()}");

        var hashes = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line[(line.LastIndexOf(',') + 1)..].Trim())
            .ToArray();
        var longestRun = 0;
        var currentRun = 0;
        string? previous = null;
        foreach (var hash in hashes)
        {
            currentRun = hash == previous ? currentRun + 1 : 1;
            longestRun = Math.Max(longestRun, currentRun);
            previous = hash;
        }

        var unique = hashes.Distinct().Count();
        Console.WriteLine($"Motion validation: samples={hashes.Length}, unique={unique}, longestRun={longestRun}.");
        if (hashes.Length < 50 || unique < 45 || longestRun > 3)
        {
            throw new InvalidOperationException("Native capture motion validation failed: decoded frames froze or repeated too long.");
        }
    }
}
