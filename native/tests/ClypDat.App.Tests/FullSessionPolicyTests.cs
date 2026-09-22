using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullSessionPolicyTests
{
    [Fact]
    public void QuotaIncludesBothContainersAndSkipsLiveWriter()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "full-session-tests", Guid.NewGuid().ToString("N"));
        var folder = LibraryLayout.VodDirectory(root, "Synthetic"); Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.mkv"); var recent = Path.Combine(folder, "recent.mp4"); var active = Path.Combine(folder, "active.mkv");
        try
        {
            foreach (var path in new[] { old, recent, active })
            {
                using (var file = File.Create(path)) file.SetLength(600L * 1024 * 1024);
                ClipInfoSidecar.Save(root, path, new ClipInfo("Synthetic", null, "Session - Synthetic"));
            }
            File.SetCreationTimeUtc(active, DateTime.UtcNow.AddHours(-3));
            File.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.SetCreationTimeUtc(recent, DateTime.UtcNow.AddHours(-1));
            using (RecordingFileOwnership.Acquire(active))
            {
                NativeReplayBuffer.EnforceFullSessionQuota(FullSessionRecorderTests.Config(root, "MKV") with { FullSessionQuotaGb = 1 });
                Assert.False(File.Exists(old)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(active));
                Assert.Throws<IOException>(() => RecordingFileOwnership.ThrowIfActive(active));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReconnectDoesNotRestartWriterForNextSessionFormatChange()
    {
        var config = FullSessionRecorderTests.Config("library", "MKV");
        Assert.Equal(ReplayBufferConfigIdentity.Serialize(config), ReplayBufferConfigIdentity.Serialize(config with { FullSessionContainer = "MP4" }));
        Assert.NotEqual(ReplayBufferConfigIdentity.Serialize(config), ReplayBufferConfigIdentity.Serialize(config with { VideoCodec = "AV1" }));
        var status = new FullSessionStatus(FullSessionState.Failed, "session.mkv", "disk full");
        var health = ReplayCaptureHealth.Unknown("worker") with { FullSession = status };
        var attach = new CaptureWorkerAttachResponse(true, "identity", health, []);
        Assert.Equal(status, JsonSerializer.Deserialize<CaptureWorkerAttachResponse>(JsonSerializer.Serialize(attach))!.Health.FullSession);
        var start = new CaptureWorkerStartAck(true, true, FullSession: status);
        Assert.Equal(status, JsonSerializer.Deserialize<CaptureWorkerStartAck>(JsonSerializer.Serialize(start))!.FullSession);
    }

    [Fact]
    public void FormatWarningAndUseMkvActionUpdateImmediatelyButNotExistingSnapshot()
    {
        var settings = new AppSettings(); var saves = 0;
        var selection = new FullSessionFormatViewModel(settings, () => saves++);
        var changed = new List<string?>(); selection.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("MKV (Recommended)", selection.Selected);
        Assert.False(selection.IsMp4);
        Assert.Equal(FullSessionFormat.Recommendation, selection.Recommendation);
        selection.Selected = "MP4";
        var startedFormat = settings.FullSessionContainer;
        Assert.True(selection.IsMp4);
        Assert.Equal(FullSessionFormat.Warning, selection.Warning);
        selection.UseMkv();
        Assert.Equal("MKV (Recommended)", selection.Selected);
        Assert.False(selection.IsMp4);
        Assert.Contains(nameof(selection.IsMp4), changed);
        Assert.Equal(2, saves);
        Assert.Equal("MP4", startedFormat);
        Assert.Equal("MKV", settings.FullSessionContainer);
    }

    [Theory]
    [InlineData("{}", "MKV")]
    public void PersistedFormatNormalizesAndRoundTrips(string json, string expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(expected, settings.FullSessionContainer);
        Assert.Equal(expected, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.FullSessionContainer);
    }

    [Theory]
    [InlineData(FullSessionState.Off, false, "Recording", true, "#F04452")]
    public void IndicatorUsesWorkerModeAndPauseState(FullSessionState state, bool paused, string label, bool flash, string color)
    {
        var health = ReplayCaptureHealth.Unknown("test") with
        {
            State = ReplayCaptureState.Healthy, StartupPhase = ReplayCaptureStartupPhase.Ready,
            FullSession = new(state, "session.mkv", "disk full"), CapturePaused = paused
        };
        var presentation = RecordingPresentation.Resolve(health, true, true);
        Assert.Equal(label, presentation.Label); Assert.Equal(flash, presentation.Flash); Assert.Equal(color, presentation.DotColor);
        if (paused) Assert.Contains("Paused:", presentation.Detail);
        if (state == FullSessionState.Failed) Assert.Contains("disk full", presentation.Detail);
    }

    [Theory]
    [InlineData("session.mkv", false)]
    public void FinalizationOptionsMatchContainer(string path, bool movflags)
    {
        var args = new List<string>(); MediaContainerOptions.AddFinalizedOptions(args, path);
        Assert.Equal(movflags, args.Contains("-movflags"));
    }
}
