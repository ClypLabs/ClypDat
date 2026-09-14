using System.Diagnostics;
using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LinuxMediaTests(Xunit.Abstractions.ITestOutputHelper outputLog)
{
    private readonly bool _initialized = Initialize();
    private static bool Initialize() { FfmpegPathResolver.EnsureBundledFfmpeg(); return true; }
    internal static ReplayBufferConfig Config => new(4, 720, 10, 0, 0, 64, 64, "", "", [], [], "", [], "Fixture", "fixture", "", "", EncoderMode: "CPU");
    [Fact]
    public void RequestedWallClockIntervalMapsToRecorderClockAndRejectsExpiredMedia()
    {
        var now = DateTime.UtcNow;
        var interval = LinuxClipInterval.FromRequest(100, now, 30, new(now.AddSeconds(-12), now.AddSeconds(-4)));
        Assert.Equal(new LinuxClipInterval(88, 96), interval);
        Assert.Equal(new LinuxClipInterval(8, 16), interval.InStaging(80, 100, .1));
        Assert.Throws<InvalidOperationException>(() => interval.InStaging(90, 100, .1));
    }
    [Fact]
    public void SeparateApplicationsAreExcludedFromMainTrackWithoutDuplicatingChat()
    {
        var tracks = LinuxAudioTrackPlan.Create(Config with { ChatAudioProcessNames = ["chat"],
            AdditionalAudioProcesses = new Dictionary<string, int> { ["chat"] = 90, ["music"] = 50 }, MicrophoneDeviceIds = ["mic"] });
        Assert.Equal(new[] { "Game", "Chat", "music", "Microphone 1" }, tracks.Select(t => t.Title));
        Assert.Contains("app-inverse:exe:chat", tracks[0].RecorderInput);
        Assert.Contains("app-inverse:exe:music", tracks[0].RecorderInput);
        Assert.Equal("name:Chat|app:exe:chat", tracks[1].RecorderInput);
        Assert.Equal(50, tracks[2].Gain);
    }
    [Fact]
    public void MicrophoneDefaultUsesRecorderInputAliasAndPreservesNamedDevices()
    {
        var tracks = LinuxAudioTrackPlan.Create(Config with { MicrophoneDeviceIds = ["default", "alsa_input.usb-microphone"] });
        Assert.Equal("name:Microphone 1|device:default_input", tracks[1].RecorderInput);
        Assert.Equal("name:Microphone 2|device:alsa_input.usb-microphone", tracks[2].RecorderInput);
    }
    [Fact]
    public async Task ExactTrimProducesOnlyRequestedDecodedFramesAndPreservesStagingOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clypdat-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv"); var output = Path.Combine(directory, "clip.mkv");
        try
        {
            await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "nullsrc=s=64x64:r=10:d=4,geq=lum='16+N*4':cb=128:cr=128",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4:sample_rate=48000", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", source], default);
            await LinuxClipFinalizer.FinalizeAsync(source, output, new(1.2, 2.7), Config, false, default);
            var raw = Path.Combine(directory, "frames.yuv");
            await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-i", output, "-an", "-pix_fmt", "yuv420p", "-f", "rawvideo", raw], default);
            var bytes = await File.ReadAllBytesAsync(raw); const int frameSize = 64 * 64 * 3 / 2;
            Assert.Equal(15 * frameSize, bytes.Length);
            Assert.InRange(bytes.Take(4096).Average(b => b), 62, 66);
            Assert.InRange(bytes.Skip(14 * frameSize).Take(4096).Average(b => b), 118, 122);
            await Assert.ThrowsAsync<IOException>(() => LinuxClipFinalizer.FinalizeAsync(source, Path.Combine(directory, "missing", "clip.mkv"), new(1, 2), Config, false, default));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task KeyframeAlignedIntervalCopiesVideoAndPartialFrameIntervalReencodes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clypdat-boundary-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            var source = Path.Combine(directory, "source.mkv"); var clip = Path.Combine(directory, "clip.mkv");
            await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=3", "-c:v", "libx264", "-bf", "0", "-g", "10", "-sc_threshold", "0", source], default);
            Assert.True(await LinuxClipFinalizer.CanCopyIntervalAsync(source, new(1, 2), "H.264", default));
            Assert.False(await LinuxClipFinalizer.CanCopyIntervalAsync(source, new(1.05, 2), "H.264", default));
            await LinuxClipFinalizer.FinalizeAsync(source, clip, new(1, 2), Config, false, default);
            using var probe = JsonDocument.Parse(await LinuxMediaProcess.RunAsync("ffprobe", ["-v", "error", "-count_frames", "-show_entries", "stream=nb_read_frames:format=duration", "-of", "json", clip], default));
            Assert.Equal("10", probe.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString());
            Assert.Equal("1.000000", probe.RootElement.GetProperty("format").GetProperty("duration").GetString());
        } finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ToneTracksKeepSeparationGainAndMonoWhileSessionVideoIsCopied()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clypdat-tone-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var source = Path.Combine(directory, "source.mkv"); var output = Path.Combine(directory, "session.mkv");
            var frequencies = new[] { 440, 880, 1320, 1760 };
            var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=2" };
            foreach (var frequency in frequencies) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=2:sample_rate=48000,aformat=channel_layouts=stereo"]);
            args.AddRange(["-map", "0:v"]);
            for (var i = 1; i <= 4; i++) args.AddRange(["-map", i + ":a"]);
            args.AddRange(["-c:v", "libx264", "-c:a", "pcm_f32le", source]);
            await LinuxMediaProcess.RunAsync("ffmpeg", args, default);
            var config = Config with { ChatAudioProcessNames = ["chat"], AdditionalAudioProcesses = new Dictionary<string, int> { ["music"] = 50 },
                MicrophoneDeviceIds = ["mic"], GameAudioVolumePercent = 50, MicrophoneVolumePercent = 50, MicrophoneChannelMode = "Mono" };
            await LinuxClipFinalizer.FinalizeAsync(source, output, null, config, true, default);
            var hashArgs = new[] { "-v", "error", "-i", source, "-map", "0:v", "-c", "copy", "-f", "hash", "-hash", "sha256", "-" };
            var inputHash = await LinuxMediaProcess.RunAsync("ffmpeg", hashArgs, default);
            hashArgs[3] = output;
            Assert.Equal(inputHash, await LinuxMediaProcess.RunAsync("ffmpeg", hashArgs, default));
            using var probe = JsonDocument.Parse(await LinuxMediaProcess.RunAsync("ffprobe", ["-v", "error", "-select_streams", "a", "-show_entries", "stream=channels:stream_tags=title", "-of", "json", output], default));
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Equal(4, streams.Length);
            Assert.Equal(new[] { 2, 2, 2, 1 }, streams.Select(e => e.GetProperty("channels").GetInt32()));
            for (var track = 0; track < 4; track++) {
                var pcm = Path.Combine(directory, track + ".f32");
                await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-i", output, "-ss", "0.5", "-t", "1", "-map", "0:a:" + track, "-ac", "1", "-ar", "48000", "-f", "f32le", pcm], default);
                var bytes = await File.ReadAllBytesAsync(pcm);
                var samples = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                var amplitudes = frequencies.Select(frequency => {
                    double real = 0, imaginary = 0;
                    for (var i = 0; i < samples.Length; i++) { var angle = 2 * Math.PI * frequency * i / 48000; real += samples[i] * Math.Cos(angle); imaginary += samples[i] * Math.Sin(angle); }
                    return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
                }).ToArray();
                Assert.InRange(amplitudes[track], track == 1 ? .115 : .057, track == 1 ? .135 : .068);
                Assert.All(amplitudes.Where((_, i) => i != track), amplitude => Assert.InRange(amplitude, 0, .003));
            }
        } finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task PausedSeekAndSustainedSkiaPlaybackUseLeasedFrames()
    {
        var path = Path.Combine(Path.GetTempPath(), "clypdat-playback-test-" + Guid.NewGuid().ToString("N") + ".mkv");
        try {
            await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=6", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "35", path], default);
            using var session = new PlaybackSession(); await session.LoadVideoAsync(path);
            var result = await session.SeekAsync(TimeSpan.FromSeconds(1), false).WaitAsync(TimeSpan.FromSeconds(15));
            outputLog.WriteLine($"seek result={result}, time={session.VideoPlayer.Time}, state={session.VideoPlayer.State}, {((LinuxEditorVideoOutput)session.Composition!).DebugState}");
            Assert.Equal(PlaybackSeekOutcome.Completed, result.Outcome);
            var output = Assert.IsType<LinuxEditorVideoOutput>(session.Composition);
            Assert.True(output.HasPresentedPicture);
            Assert.InRange(session.VideoPlayer.Time, 950, 1100);
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1280, 720));
            session.VideoPlayer.Play();
            var watch = Stopwatch.StartNew(); double renderMilliseconds = 0; var draws = 0;
            var decoded = output.ReadStatus().DecodedPicture;
            while (watch.Elapsed < TimeSpan.FromSeconds(3)) {
                if (output.Acquire() is { } frame) {
                    var start = Stopwatch.GetTimestamp();
                    try { output.Draw(surface.Canvas, frame, new Avalonia.Rect(0, 0, 1280, 720)); }
                    finally { output.Release(frame); }
                    renderMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds; draws++;
                }
                await Task.Delay(16);
            }
            var count = output.ReadStatus().DecodedPicture - decoded;
            outputLog.WriteLine($"720p software Skia fixture: {count / watch.Elapsed.TotalSeconds:0.0} decoded FPS, {renderMilliseconds / Math.Max(draws, 1):0.00} ms/draw, {draws} draws.");
            Assert.True(count >= 50, "Playback stalled during the synthetic render fixture.");
            session.SetPlaybackRate(1.5);
            Assert.InRange(session.VideoPlayer.Rate, 1.49f, 1.51f);
            session.VideoPlayer.Stop();
        } finally { File.Delete(path); }
    }
    [Fact]
    public async Task LibVlcCallbacksDecodeSyntheticVideoWithoutAnX11Window()
    {
        var path = Path.Combine(Path.GetTempPath(), "clypdat-editor-test-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            await LinuxMediaProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=128x72:r=15:d=3", "-c:v", "libx264", path], default);
            using var session = new PlaybackSession();
            await session.LoadVideoAsync(path);
            Assert.IsType<LinuxEditorVideoOutput>(session.Composition);
            session.VideoPlayer.Play();
            var watch = Stopwatch.StartNew();
            while (!session.Composition!.HasPresentedPicture && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.True(session.Composition!.HasPresentedPicture);
            var output = (LinuxEditorVideoOutput)session.Composition;
            var frame = output.Acquire(); Assert.NotNull(frame);
            Assert.Equal(128, frame!.Width); Assert.Equal(72, frame.Height);
            output.BeginSeek(TimeSpan.FromSeconds(1));
            Assert.NotEqual(output.Generation, frame.Generation);
            output.Release(frame);
            session.VideoPlayer.Stop();
        }
        finally { File.Delete(path); }
    }
}
