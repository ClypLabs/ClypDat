using System.Diagnostics;
using Avalonia;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipHoverPreviewControllerTests
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
    public void BuildDecoderArguments_NormalClip_UsesCoverThenCenterCrop()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments(
            "clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(3)), 60, new PixelSize(320, 180));

        Assert.Equal(
            "fps=60,scale=w=320:h=180:flags=bilinear:force_original_aspect_ratio=increase,crop=320:180:(in_w-out_w)/2:(in_h-out_h)/2",
            Filter(arguments));
    }

    [Fact]
    public void BuildDecoderArguments_EditedCrop_UsesCropThenContainAndCenterPad()
    {
        var arguments = ClipHoverPreviewController.BuildDecoderArguments(
            "clip.mp4", (TimeSpan.Zero, TimeSpan.FromSeconds(3)), 60, new PixelSize(320, 180), "crop=900:900:10:20");

        Assert.Equal(
            "fps=60,crop=900:900:10:20,scale=w=320:h=180:flags=bilinear:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2",
            Filter(arguments));
    }

    [Fact]
    public async Task RapidReentry_RestartsFromStartWithoutDetachingPresenter()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        using var controller = new ClipHoverPreviewController();
        var presenter = new FakePresenter();

        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.FrameCount >= 8);

        controller.PointerLeft(fixture.Clip);
        var framesAtExit = presenter.FrameCount;
        await Task.Delay(75);
        Assert.True(presenter.FrameCount > framesAtExit);
        Assert.DoesNotContain(false, presenter.Attachments);

        var zeroProgressBeforeRestart = presenter.ZeroProgressCount;
        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.ZeroProgressCount > zeroProgressBeforeRestart && presenter.FrameCount >= framesAtExit + 8);

        Assert.DoesNotContain(false, presenter.Attachments);
        Assert.Equal(0, presenter.ReleaseCount);

        controller.PointerLeft(fixture.Clip);
        await WaitUntilAsync(() => presenter.ReleaseCount == 1);
    }

    [Fact]
    public async Task SustainedExit_KeepsPreviewAttachedThroughGraceThenReleasesIt()
    {
        await using var fixture = await PreviewFixture.CreateAsync();
        using var controller = new ClipHoverPreviewController();
        var presenter = new FakePresenter();

        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.FrameCount >= 6);

        controller.PointerLeft(fixture.Clip);
        var framesAtExit = presenter.FrameCount;
        await Task.Delay(75);

        Assert.True(presenter.FrameCount > framesAtExit);
        Assert.DoesNotContain(false, presenter.Attachments);
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

    private static string Filter(IReadOnlyList<string> arguments) => arguments[Array.IndexOf(arguments.ToArray(), "-vf") + 1];

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

        public PreviewPresentationPath Path => PreviewPresentationPath.Software;
        public int FrameCount => Volatile.Read(ref _frames);
        public int ReleaseCount => Volatile.Read(ref _releases);
        public int ZeroProgressCount => Volatile.Read(ref _zeroProgress);
        public IReadOnlyList<bool> Attachments { get { lock (_gate) return _attachments.ToArray(); } }

        public ValueTask ActivateSessionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SetAttachedAsync(bool attached)
        {
            lock (_gate) _attachments.Add(attached);
            return ValueTask.CompletedTask;
        }
        public ValueTask SetProgressAsync(double progress)
        {
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
            info.ArgumentList.Add("-y");
            info.ArgumentList.Add(path);
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return new PreviewFixture(path, CreateClip(path, "hover"));
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
