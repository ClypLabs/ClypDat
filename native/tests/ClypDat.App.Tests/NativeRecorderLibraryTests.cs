using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeRecorderLibraryTests
{
    [Fact]
    public void BundledEngineLoadsAndRejectsIncompleteRecordingContract()
    {
        var config = new ReplayBufferConfig(60, 1080, 60, 0, 0, 1920, 1080,
            "", "", [], [], "", [], "Synthetic", "synthetic.exe", "", "");
        Assert.False(NativeReplayEngine.TryCreate(config, out var engine, out var error));
        Assert.Null(engine);
        Assert.Contains("native recorder is incomplete; missing capabilities:", error);
    }

    [Fact]
    public void MissingBundleReportsAbsoluteComponentPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "missing-recorder-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<DllNotFoundException>(() => NativeRecorderLibrary.LoadBundle(directory));
        Assert.Contains(Path.Combine(directory, "ffmpeg", "avutil-60.dll"), error.Message);
        Assert.Contains("Reinstall ClypDat", error.Message);
    }

    private static uint Version(IntPtr handle, string export) => export switch
    {
        "avutil_version" => 0x3c1a66,
        "swresample_version" => 0x060366,
        "swscale_version" => 0x090566,
        "avcodec_version" => 0x3e1c66,
        "avformat_version" => 0x3e0c66,
        _ => throw new InvalidOperationException(export)
    };

    [Fact]
    public void LoadsAbsoluteDependenciesBeforeEngine()
    {
        var paths = new List<string>();
        var releases = new List<IntPtr>();
        var root = Path.GetFullPath("recorder-fixture");
        var handle = NativeRecorderLibrary.LoadBundle(root, path =>
        {
            paths.Add(path);
            return new IntPtr(paths.Count);
        }, Version, releases.Add);
        Assert.Equal(new IntPtr(6), handle);
        Assert.Equal(new[] { "avutil-60.dll", "swresample-6.dll", "swscale-9.dll", "avcodec-62.dll", "avformat-62.dll" },
            paths.Take(5).Select(Path.GetFileName));
        Assert.All(paths.Take(5), path => Assert.Equal(Path.Combine(root, "ffmpeg"), Path.GetDirectoryName(path)));
        Assert.Equal(Path.Combine(root, "ClypDat.Capture.Native.dll"), paths.Last());
        Assert.Empty(releases); // Handles survive every engine instance.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void MissingComponentUnwindsOnlyAcquiredDependencies(int missing)
    {
        var loads = 0;
        var releases = new List<IntPtr>();
        var error = Assert.Throws<DllNotFoundException>(() => NativeRecorderLibrary.LoadBundle("recorder-fixture",
            path => ++loads == missing ? IntPtr.Zero : new IntPtr(loads), Version, releases.Add));
        Assert.Contains("Reinstall ClypDat", error.Message);
        Assert.Equal(missing, loads);
        Assert.Equal(Enumerable.Range(1, missing - 1).Reverse().Select(value => new IntPtr(value)), releases);
    }

    [Fact]
    public void VersionMismatchPreventsEngineLoadAndUnwinds()
    {
        var loads = 0;
        var releases = new List<IntPtr>();
        var error = Assert.Throws<BadImageFormatException>(() => NativeRecorderLibrary.LoadBundle("recorder-fixture",
            path => new IntPtr(++loads), (handle, export) => export == "avcodec_version" ? 0 : Version(handle, export), releases.Add));
        Assert.Contains("avcodec-62.dll", error.Message);
        Assert.Equal(4, loads);
        Assert.Equal(new IntPtr[] { 4, 3, 2, 1 }, releases);
    }

    [Fact]
    public void MissingVersionExportUnwindsAcquiredModule()
    {
        var releases = new List<IntPtr>();
        Assert.Throws<EntryPointNotFoundException>(() => NativeRecorderLibrary.LoadBundle("recorder-fixture",
            path => new IntPtr(1), (handle, export) => throw new EntryPointNotFoundException(export), releases.Add));
        Assert.Equal(new IntPtr[] { 1 }, releases);
    }
}
