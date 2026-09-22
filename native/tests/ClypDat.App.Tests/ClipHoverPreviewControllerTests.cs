using System.Diagnostics;
using Avalonia;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipHoverPreviewControllerTests
{
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
        controller.Request(fixture.Clip, true, presenter, fixture.Size);
        await WaitUntilAsync(() => presenter.ZeroProgressCount > zeroProgressBeforeRestart && presenter.FirstFrames.Count == 2);

        Assert.Equal([true, false, true], presenter.Attachments.Take(3));
        Assert.True(presenter.FramesPresentedWhileDetached > 0);
        Assert.Equal(firstFrameBeforeRestart, presenter.FirstFrames[1]);
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
        private bool _attached;
        private int _framesInSession;
        private int _framesPresentedWhileDetached;
        private readonly List<byte[]> _firstFrames = [];

        public PreviewPresentationPath Path => PreviewPresentationPath.Software;
        public int FrameCount => Volatile.Read(ref _frames);
        public int ReleaseCount => Volatile.Read(ref _releases);
        public int ZeroProgressCount => Volatile.Read(ref _zeroProgress);
        public int FramesPresentedWhileDetached => Volatile.Read(ref _framesPresentedWhileDetached);
        public IReadOnlyList<bool> Attachments { get { lock (_gate) return _attachments.ToArray(); } }
        public IReadOnlyList<byte[]> FirstFrames { get { lock (_gate) return _firstFrames.Select(frame => frame.ToArray()).ToArray(); } }

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
            if (progress == 0)
            {
                lock (_gate) _framesInSession = 0;
                Interlocked.Increment(ref _zeroProgress);
            }
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
