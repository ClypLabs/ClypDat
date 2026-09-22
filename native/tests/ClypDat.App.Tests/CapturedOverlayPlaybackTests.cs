using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Threading;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CapturedOverlayPlaybackTests
{
    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void ValidCameraCoverage_ContinuesAtRecordedCadenceAcrossNormalPlaybackTicks()
    {
        using var fixture = new CameraFixture();
        AvaloniaTestThread.Run(() =>
        {
            using var playback = new CapturedOverlayPlayback();
            var received = 0;
            var missing = 0;
            var frames = new List<Avalonia.Media.Imaging.Bitmap>();
            playback.FrameReady += bitmap =>
            {
                if (bitmap is null) missing++;
                else { received++; frames.Add(bitmap); }
            };
            var layer = new ClipOverlayLayer("Camera", true, Assets: [new ClipOverlayAsset("camera.mp4", 0, 2)]);
            playback.Request(fixture.Root, layer, 0);
            PumpUntil(() => received > 0, TimeSpan.FromSeconds(8));
            var initial = received;
            for (var index = 1; index <= 30; index++)
            {
                playback.Request(fixture.Root, layer, index / 60d);
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < 17) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            }
            Assert.Equal(0, missing);
            foreach (var frame in frames) frame.Dispose();
            Assert.True(received - initial >= 27, $"Expected recorded 60fps cadence; received {received - initial} camera frames across 30 ticks.");
        }, TimeSpan.FromSeconds(20), "Camera playback regression timed out.");
    }

    [Fact]
    public void DroppedCaptureFramesCompressTheClipTimelineAndSpeedTheSourceToMatch()
    {
        // Video PTS are assigned an ideal constant rate, so a clip that dropped
        // frames covers more wall-clock time than its media duration. Camera
        // segments are placed by wall-clock, so they carry the clip-time scale
        // and the inverse as PlaybackRate - the file itself still runs in real
        // time. Without this the camera slides steadily later across the clip.
        const double scale = 0.99;  // 600ms lost over a 60s capture
        var asset = new ClipOverlayAsset("30.mp4", StartSeconds: 60 * scale, EndSeconds: 62 * scale,
            SourceOffsetSeconds: 0, PlaybackRate: 1 / scale);

        // One second of clip time into the segment is 1/scale seconds of camera.
        var source = CapturedOverlayPlayback.SourceSeconds(asset, 60 * scale + 1);

        Assert.Equal(1 / scale, source, 6);
        Assert.Equal(CapturedOverlayPlayback.FrameIndex(1 / scale, 60), CapturedOverlayPlayback.FrameIndex(source, 60));
    }

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!completed() && elapsed.Elapsed < timeout) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(completed(), "Camera frame was never delivered during valid coverage.");
    }

    internal sealed class CameraFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "clypdat-camera-playback-" + Guid.NewGuid().ToString("N"));
        public CameraFixture()
        {
            Directory.CreateDirectory(Root);
            FfmpegPathResolver.EnsureBundledFfmpeg();
            var start = FfmpegPathResolver.Ffmpeg();
            start.CreateNoWindow = true;
            start.RedirectStandardError = true;
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60:duration=2", "-c:v", "libx264", "-preset", "ultrafast", "-bf", "0", "-g", "120", "-pix_fmt", "yuv420p", "-y", Path.Combine(Root, "camera.mp4") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(15000), "Synthetic camera encoding timed out.");
            Assert.True(process.ExitCode == 0, errors);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
