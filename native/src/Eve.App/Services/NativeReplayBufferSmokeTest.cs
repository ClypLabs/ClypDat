namespace Eve.App.Services;

// Throwaway validation harness for Phase 1 of the native capture engine (see plan).
// Invoked via `EVE.exe --test-native-capture` (Program.cs) - not part of the normal
// app flow. Exercises the real NativeReplayBuffer class end-to-end: start, let the
// ring buffer accumulate for a few seconds, save, stop.
internal static class NativeReplayBufferSmokeTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("NativeReplayBuffer smoke test starting...");

        var config = new ReplayBufferConfig(
            DurationSeconds: 30,
            MaxHeight: 1080,
            FrameRate: 60,
            CaptureX: 0,
            CaptureY: 0,
            CaptureWidth: 1920,
            CaptureHeight: 1080,
            ChatAudioDeviceName: string.Empty,
            ChatAudioDeviceId: string.Empty,
            ChatAudioProcessName: string.Empty,
            MicrophoneDeviceId: string.Empty,
            MicrophoneDeviceName: string.Empty,
            GameAudioExcludedProcesses: Array.Empty<string>(),
            GameDisplayName: "Native Capture Test",
            GameExecutableName: string.Empty,
            GameWindowTitle: string.Empty,
            GameWindowClass: string.Empty);

        var buffer = new NativeReplayBuffer(() => config);

        Console.WriteLine("Starting capture...");
        await buffer.StartAsync();
        Console.WriteLine($"Recording: {buffer.IsRecording}");

        Console.WriteLine("Letting ring buffer accumulate for 8 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(8));

        var outputFolder = Path.Combine(Path.GetTempPath(), "eve-native-capture-test");
        Directory.CreateDirectory(outputFolder);

        Console.WriteLine("Saving replay...");
        var outputPath = await buffer.SaveReplayAsync(outputFolder);
        Console.WriteLine($"Saved: {outputPath}");

        var fileInfo = new FileInfo(outputPath);
        Console.WriteLine($"File size: {fileInfo.Length} bytes");

        Console.WriteLine("Stopping capture...");
        await buffer.StopAsync();
        buffer.Dispose();

        Console.WriteLine("Smoke test complete.");
    }
}
