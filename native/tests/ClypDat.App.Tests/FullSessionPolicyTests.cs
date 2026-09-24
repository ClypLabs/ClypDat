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
                FullSessionPublication.EnforceQuota(TestReplayConfiguration.Create(root, "MKV") with { FullSessionQuotaGb = 1 });
                Assert.False(File.Exists(old)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(active));
                Assert.Throws<IOException>(() => RecordingFileOwnership.ThrowIfActive(active));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReconnectDoesNotRestartWriterForNextSessionFormatChange()
    {
        var config = TestReplayConfiguration.Create("library", "MKV");
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

    [Fact]
    public void Mp4WarningCanBeIgnoredOrHiddenPermanently()
    {
        var settings = new AppSettings { FullSessionContainer = "MP4" }; var saves = 0;
        var selection = new FullSessionFormatViewModel(settings, () => saves++);
        Assert.True(selection.IsWarningVisible);
        selection.IgnoreWarning();
        Assert.False(selection.IsWarningVisible);
        Assert.False(settings.HideFullSessionMp4Warning);

        var restartedSelection = new FullSessionFormatViewModel(settings, () => saves++);
        Assert.True(restartedSelection.IsWarningVisible);
        restartedSelection.HideWarningPermanently();
        Assert.True(settings.HideFullSessionMp4Warning);
        Assert.False(restartedSelection.IsWarningVisible);
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData("{}", "MKV")]
    [InlineData("{\"FullSessionContainer\":\"MP4\"}", "MP4")]
    [InlineData("{\"FullSessionContainer\":\"mkv\"}", "MKV")]
    [InlineData("{\"FullSessionContainer\":\"bad\"}", "MKV")]
    [InlineData("{\"FullSessionContainer\":null}", "MKV")]
    public void PersistedFormatNormalizesAndRoundTrips(string json, string expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(expected, settings.FullSessionContainer);
        Assert.Equal(expected, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.FullSessionContainer);
    }

    [Theory]
    [InlineData("Manual", true, false)]
    [InlineData("FullSession", true, true)]
    [InlineData("Off", false, false)]
    public void CustomModeAndQualityTakePrecedence(string mode, bool capture, bool session)
    {
        var settings = new AppSettings { FullSessionRecordingEnabled = true, ReplayVideoCodec = "H.264", FullSessionVideoCodec = "H.264" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            Groups = [CustomGameSettingsResolver.RecordingModeGroup, CustomGameSettingsResolver.QualityGroup],
            RecordingMode = mode, ReplayVideoCodec = "AV1"
        };
        var effective = CustomGameSettingsResolver.Resolve(settings, "game.exe");
        Assert.Equal(capture, effective.RecordingEnabled);
        Assert.Equal(session, effective.FullSessionRecordingEnabled);
        Assert.Equal("AV1", effective.ReplayVideoCodec);
        Assert.Equal(effective.ReplayVideoCodec, effective.FullSessionVideoCodec);
        Assert.Equal("MKV", settings.FullSessionContainer);
    }

    [Theory]
    [InlineData(FullSessionState.Off, false, "Recording", true, "#F04452")]
    [InlineData(FullSessionState.Recording, false, "Full Session Recording", true, "#F04452")]
    [InlineData(FullSessionState.Recording, true, "Full Session Recording", true, "#F04452")]
    [InlineData(FullSessionState.Off, true, "Recording", true, "#F04452")]
    [InlineData(FullSessionState.Starting, false, "Starting", false, "Transparent")]
    [InlineData(FullSessionState.Stopping, false, "Stopping", false, "Transparent")]
    [InlineData(FullSessionState.Failed, false, "Recording", true, "#F04452")]
    public void IndicatorUsesWorkerModeAndPauseState(FullSessionState state, bool paused, string label, bool flash, string color)
    {
        var health = ReplayCaptureHealth.Unknown("test") with
        {
            State = ReplayCaptureState.Healthy, StartupPhase = ReplayCaptureStartupPhase.Ready,
            FullSession = new(state, "session.mkv", "disk full"), CapturePaused = paused
        };
        var presentation = RecordingPresentation.Resolve(health, true, true);
        Assert.Equal(label, presentation.Label); Assert.Equal(flash, presentation.Flash); Assert.Equal(color, presentation.DotColor);
        if (paused) Assert.Contains("Video capture paused.", presentation.Detail);
        if (state == FullSessionState.Failed) Assert.Contains("disk full", presentation.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonCaptureStatesNeverFlash(bool paused)
    {
        var health = ReplayCaptureHealth.Unknown("test") with { State = ReplayCaptureState.Healthy, FullSession = new(FullSessionState.Recording), CapturePaused = paused };
        Assert.False(RecordingPresentation.Resolve(health, true, false).Flash);
        Assert.False(RecordingPresentation.Resolve(health, false, true).Flash);
        Assert.False(RecordingPresentation.Resolve(health with { State = ReplayCaptureState.Recovering }, true, true).Flash);
        Assert.False(RecordingPresentation.Resolve(health with { StartupPhase = ReplayCaptureStartupPhase.WaitingForForeground }, true, true).Flash);
        Assert.False(RecordingPresentation.Resolve(health, true, true, stopping: true).Flash);
        Assert.False(RecordingPresentation.Resolve(health with { State = ReplayCaptureState.Stopping }, true, true).Flash);
        Assert.False(RecordingPresentation.Resolve(health with { State = ReplayCaptureState.Unknown }, true, true).Flash);
    }

    [Theory]
    [InlineData("session.mkv", false)]
    [InlineData("session.MKV", false)]
    [InlineData("session.mp4", true)]
    public void FinalizationOptionsMatchContainer(string path, bool movflags)
    {
        var args = new List<string>(); MediaContainerOptions.AddFinalizedOptions(args, path);
        Assert.Equal(movflags, args.Contains("-movflags"));
    }
}
