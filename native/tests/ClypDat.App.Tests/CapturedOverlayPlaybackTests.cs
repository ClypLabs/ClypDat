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

    // The three segment shapes a clip produces. Camera playback used to divide a
    // source-time offset by the segment's clip-visible span and scale it by the
    // whole file's frame count, so only fully contained segments landed near the
    // right frame: a partially included first segment pinned to its opening
    // frames and a truncated final one ran proportionally fast.
    [Fact]
    public void PartiallyIncludedFirstSegment_ReadsFromWhereTheClipActuallyStarts()
    {
        // The clip starts 1.6s into a 2s segment, so it shows only its last 0.4s.
        var asset = new ClipOverlayAsset("0.mp4", StartSeconds: 0, EndSeconds: .4, SourceOffsetSeconds: 1.6);

        Assert.Equal(1.6, CapturedOverlayPlayback.SourceSeconds(asset, 0), 6);
        Assert.Equal(1.8, CapturedOverlayPlayback.SourceSeconds(asset, .2), 6);
        // Not clamped to the 0.4s visible span, which is what pinned it before.
        Assert.Equal(48, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, 0), 60));
        Assert.Equal(54, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, .2), 60));
    }

    [Fact]
    public void FullyContainedSegment_MapsClipTimeStraightThrough()
    {
        var asset = new ClipOverlayAsset("1.mp4", StartSeconds: .4, EndSeconds: 2.4);

        Assert.Equal(0, CapturedOverlayPlayback.SourceSeconds(asset, .4), 6);
        Assert.Equal(1, CapturedOverlayPlayback.SourceSeconds(asset, 1.4), 6);
        Assert.Equal(0, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, .4), 60));
        Assert.Equal(30, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, 1.4), 60));
    }

    [Fact]
    public void TruncatedFinalSegment_RunsAtRealTimeRatherThanStretchingToTheClipEnd()
    {
        // The clip ends 0.5s into this segment; the file still holds a full 2s.
        var asset = new ClipOverlayAsset("2.mp4", StartSeconds: 2.4, EndSeconds: 2.9);

        Assert.Equal(.25, CapturedOverlayPlayback.SourceSeconds(asset, 2.65), 6);
        Assert.Equal(8, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, 2.65), 60));
        // Half a second in is frame 15, not the file's last frame.
        Assert.Equal(15, CapturedOverlayPlayback.FrameIndex(CapturedOverlayPlayback.SourceSeconds(asset, 2.9), 60));
    }

    [Fact]
    public void FrameIndex_StaysInsideWhatWasDecoded()
    {
        Assert.Equal(0, CapturedOverlayPlayback.FrameIndex(-5, 60));
        Assert.Equal(59, CapturedOverlayPlayback.FrameIndex(600, 60));
        Assert.Equal(0, CapturedOverlayPlayback.FrameIndex(1, 0));
    }

    [Fact]
    public void PlaybackRate_ScalesSourceTime()
    {
        var asset = new ClipOverlayAsset("0.mp4", StartSeconds: 0, EndSeconds: 2, PlaybackRate: 2);

        Assert.Equal(2, CapturedOverlayPlayback.SourceSeconds(asset, 1), 6);
        // A rate that cannot be honoured must not produce NaN offsets.
        Assert.Equal(1, CapturedOverlayPlayback.SourceSeconds(asset with { PlaybackRate = 0 }, 1), 6);
        Assert.Equal(1, CapturedOverlayPlayback.SourceSeconds(asset with { PlaybackRate = double.NaN }, 1), 6);
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
