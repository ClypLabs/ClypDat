using System.Diagnostics;
using Avalonia;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

public sealed class ClipHoverPreviewControllerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1.0, 320, 180)]
    [InlineData(1.25, 416, 234)]
    [InlineData(1.5, 480, 270)]
    public void ResolvePreviewSize_DisplayScaling_UsesExactEvenSixteenByNineCanvas(
        double renderScaling, int expectedWidth, int expectedHeight)
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(320, 180), renderScaling);

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), size);
        Assert.True(size.Width <= ClipHoverPreviewController.MaximumPreviewWidth);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
        Assert.Equal(size.Width * 9, size.Height * 16);
    }

    [Fact]
    public void ResolvePreviewSize_FractionalTileHeight_UsesWidthDerivedSixteenByNineCanvas()
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(220, 123.75), 1.25);

        Assert.Equal(new PixelSize(288, 162), size);
        Assert.Equal(size.Width * 9, size.Height * 16);
    }

    [Fact]
    public void ResolvePreviewSize_LargeTile_CapsExactCanvasAt640By360()
    {
        var size = ClipHoverPreviewController.ResolvePreviewSize(new Size(1000, 562.5), 1.5);

        Assert.Equal(new PixelSize(640, 360), size);
    }

    [Fact]
    public async Task NativeDecoder_NormalClip_CoversTheCanvasEdgeToEdge()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        var frame = fixture.DecodeFirstFrame(crop: null);

        // testsrc2 has no black at its edges, so a covered canvas has none either.
        Assert.False(IsOpaqueBlack(frame, fixture.Size, 0, fixture.Size.Height / 2));
        Assert.False(IsOpaqueBlack(frame, fixture.Size, fixture.Size.Width - 1, fixture.Size.Height / 2));
    }

    [Fact]
    public async Task NativeDecoder_EditedCrop_FitsInsideWithBlackBars()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        // A square crop on a 16:9 canvas: pillarboxed, bars left and right.
        var frame = fixture.DecodeFirstFrame(crop: (80, 0, 180, 180));

        Assert.True(IsOpaqueBlack(frame, fixture.Size, 0, fixture.Size.Height / 2));
        Assert.True(IsOpaqueBlack(frame, fixture.Size, fixture.Size.Width - 1, fixture.Size.Height / 2));
        Assert.False(IsOpaqueBlack(frame, fixture.Size, fixture.Size.Width / 2, fixture.Size.Height / 2));
    }

    [Fact]
    public async Task NativeDecoder_EmitsOneFramePerSixtiethOfTheRange()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        var frames = await fixture.DecodeFramePrefixesAsync();

        Assert.Equal(ClipHoverPreviewController.FramesPerLoop(fixture.Clip.HoverPreviewRange.Duration), frames.Count);
        Assert.Equal(120, frames.Count);
    }

    private static bool IsOpaqueBlack(byte[] rgba, PixelSize size, int x, int y)
    {
        var offset = (y * size.Width + x) * 4;
        return rgba[offset] < 8 && rgba[offset + 1] < 8 && rgba[offset + 2] < 8 && rgba[offset + 3] == 255;
    }

    [Fact]
    public async Task RapidReentry_HidesImmediatelyThenStagesRestartFrameBeforeAttaching()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        using var controller = new ClipHoverPreviewController();
        var presenter = new FakePresenter();

        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.FrameCount >= 8);

        controller.PointerLeft(fixture.Clip);
        await WaitUntilAsync(() => presenter.Attachments.Contains(false));
        var framesAfterExit = presenter.FrameCount;
        await Task.Delay(75);
        Assert.Equal(framesAfterExit, presenter.FrameCount);
        Assert.Equal(0, presenter.ReleaseCount);

        var zeroProgressBeforeRestart = presenter.ZeroProgressCount;
        var firstFrameBeforeRestart = presenter.FirstFrames.Single();
        var positionBeforeRestart = presenter.Progress;
        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.ZeroProgressCount > zeroProgressBeforeRestart && presenter.FirstFrames.Count == 2 && presenter.FirstProgress.Count == 2);

        Assert.Equal([true, false, true], presenter.Attachments.Take(3));
        Assert.True(presenter.FramesPresentedWhileDetached > 0);
        // The latest-frame mailbox can skip frames before its first consumer
        // runs. Two sessions need not first present the same decoded frame.
        // Verify fresh-stream pixels at each reported position, and a rewind,
        // while retaining the stage-before-attach checks above.
        var reference = await fixture.DecodeFramePrefixesAsync();
        var frameCount = (int)Math.Ceiling(fixture.Clip.HoverPreviewRange.Duration.TotalSeconds * ClipHoverPreviewController.MaximumFramesPerSecond);
        var indices = presenter.FirstProgress.Select(progress => (int)Math.Round(progress * frameCount) - 1).ToArray();
        output.WriteLine($"First displayed frame indices: initial={indices[0]}, restart={indices[1]}.");
        Assert.All(indices, index => Assert.InRange(index, 0, reference.Count - 1));
        Assert.Equal(reference[indices[0]], firstFrameBeforeRestart);
        Assert.Equal(reference[indices[1]], presenter.FirstFrames[1]);
        Assert.True(presenter.FirstProgress[1] < positionBeforeRestart, "Warm reentry must rewind the decoder.");
        Assert.Equal(0, presenter.ReleaseCount);

        controller.PointerLeft(fixture.Clip);
        await WaitUntilAsync(() => presenter.ReleaseCount == 1);
    }

    [Fact]
    public async Task SustainedExit_HidesAndStopsFramesBeforeGraceReleasesResources()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        using var controller = new ClipHoverPreviewController();
        var presenter = new FakePresenter();

        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.FrameCount >= 6);

        controller.PointerLeft(fixture.Clip);
        await WaitUntilAsync(() => presenter.Attachments.Contains(false));
        var framesAfterExit = presenter.FrameCount;
        await Task.Delay(75);

        Assert.Equal(framesAfterExit, presenter.FrameCount);
        Assert.Equal(0, presenter.ReleaseCount);

        await WaitUntilAsync(() => presenter.ReleaseCount == 1);
        Assert.Contains(false, presenter.Attachments);
    }

    [Fact]
    public async Task DifferentTile_ReplacesOldPresenterImmediately()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        using var controller = new ClipHoverPreviewController();
        var first = new FakePresenter();
        var second = new FakePresenter();
        var otherClip = fixture.CreateClip("other");

        controller.Request(fixture.Clip, true, first, fixture.Size);
        await WaitUntilAsync(() => first.FrameCount >= 6);

        controller.Request(otherClip, true, second, fixture.Size);
        await WaitUntilAsync(() => first.ReleaseCount == 1 && second.FrameCount >= 4);

        Assert.Contains(false, first.Attachments);

        controller.PointerLeft(otherClip);
        await WaitUntilAsync(() => second.ReleaseCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakePresenter : IClipPreviewPresenter
    {
        private readonly object _gate = new();
        private readonly List<bool> _attachments = [];
        private int _frames;
        private int _releases;
        private int _zeroProgress;
        private bool _attached;
        private int _framesInSession;
        private int _framesPresentedWhileDetached;
        private readonly List<byte[]> _firstFrames = [];
        private readonly List<double> _firstProgress = [];
        private double _progress;

        public PreviewPresentationPath Path => PreviewPresentationPath.Software;
        public int FrameCount => Volatile.Read(ref _frames);
        public int ReleaseCount => Volatile.Read(ref _releases);
        public int ZeroProgressCount => Volatile.Read(ref _zeroProgress);
        public int FramesPresentedWhileDetached => Volatile.Read(ref _framesPresentedWhileDetached);
        public IReadOnlyList<bool> Attachments { get { lock (_gate) return _attachments.ToArray(); } }
        public IReadOnlyList<byte[]> FirstFrames { get { lock (_gate) return _firstFrames.Select(frame => frame.ToArray()).ToArray(); } }

        public IReadOnlyList<double> FirstProgress { get { lock (_gate) return _firstProgress.ToArray(); } }
        public double Progress { get { lock (_gate) return _progress; } }

        public ValueTask ActivateSessionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SetAttachedAsync(bool attached)
        {
            lock (_gate)
            {
                _attached = attached;
                _attachments.Add(attached);
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask SetProgressAsync(double progress)
        {
            lock (_gate)
            {
                _progress = progress;
                if (progress == 0) _framesInSession = 0;
                else if (_firstProgress.Count < _firstFrames.Count) _firstProgress.Add(progress);
            }
            if (progress == 0) Interlocked.Increment(ref _zeroProgress);
            return ValueTask.CompletedTask;
        }
        public ValueTask ReleaseResourcesAsync()
        {
            Interlocked.Increment(ref _releases);
            return ValueTask.CompletedTask;
        }
        public ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_attached) Interlocked.Increment(ref _framesPresentedWhileDetached);
                if (_framesInSession++ == 0) _firstFrames.Add(rgba.Span[..32].ToArray());
            }
            Interlocked.Increment(ref _frames);
            return ValueTask.FromResult(new PreviewPresentResult(PreviewPresentationPath.Software, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PreviewFixture : IAsyncDisposable
    {
        private PreviewFixture(string path, ClipCardViewModel clip)
        {
            Path = path;
            Clip = clip;
        }

        public string Path { get; }
        public ClipCardViewModel Clip { get; }
        public PixelSize Size { get; } = new(160, 90);

        public static async Task<PreviewFixture> CreateAsync()
        {
            FfmpegPathResolver.EnsureBundledFfmpeg();
            Assert.True(FfmpegPathResolver.IsAvailable, "Bundled FFmpeg is unavailable to controller tests.");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clypdat-hover-{Guid.NewGuid():N}.mp4");
            var info = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-loglevel");
            info.ArgumentList.Add("error");
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("lavfi");
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add("testsrc2=size=320x180:rate=60");
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add("2");
            info.ArgumentList.Add("-an");
            info.ArgumentList.Add("-c:v");
            info.ArgumentList.Add("mpeg4");
            info.ArgumentList.Add("-g");
            info.ArgumentList.Add("1");
            info.ArgumentList.Add("-y");
            info.ArgumentList.Add(path);
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return new PreviewFixture(path, CreateClip(path, "hover"));
        }

        // Every frame of one pass, in order: the decoder runs unpaced, so each
        // frame waits to be taken and none is replaced.
        public Task<IReadOnlyList<byte[]>> DecodeFramePrefixesAsync() => Task.Run<IReadOnlyList<byte[]>>(() =>
        {
            var range = Clip.HoverPreviewRange;
            using var decoder = NativeClipPreview.Open(Path, range.Start, range.Duration, Size.Width, Size.Height,
                ClipHoverPreviewController.MaximumFramesPerSecond, crop: null, paced: false);
            var count = ClipHoverPreviewController.FramesPerLoop(range.Duration);
            var buffer = new byte[Size.Width * Size.Height * 4];
            var prefixes = new List<byte[]>(count);
            ulong last = 0;
            while (prefixes.Count < count)
            {
                var taken = decoder.Take(last, buffer, 1000);
                Assert.True(taken.Sequence != 0, $"Decoder stalled after {prefixes.Count} frames: {decoder.Error()}");
                Assert.Equal(last + 1, taken.Sequence);
                last = taken.Sequence;
                prefixes.Add(buffer.AsSpan(0, 32).ToArray());
            }
            return prefixes;
        });

        public byte[] DecodeFirstFrame((int X, int Y, int Width, int Height)? crop)
        {
            var range = Clip.HoverPreviewRange;
            using var decoder = NativeClipPreview.Open(Path, range.Start, range.Duration, Size.Width, Size.Height,
                ClipHoverPreviewController.MaximumFramesPerSecond, crop, paced: false);
            var buffer = new byte[Size.Width * Size.Height * 4];
            var taken = decoder.Take(0, buffer, 1000);
            Assert.True(taken.Sequence == 1, $"No first frame: {decoder.Error()}");
            return buffer;
        }

        public ClipCardViewModel CreateClip(string name) => CreateClip(Path, name);

        private static ClipCardViewModel CreateClip(string path, string name) => new(
            new MediaFileInfo(name, path, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2),
                new FileInfo(path).Length, string.Empty, Array.Empty<MediaTrackInfo>(), 320, 180, 60),
            System.IO.Path.GetDirectoryName(path)!);

        public ValueTask DisposeAsync()
        {
            try { File.Delete(Path); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
