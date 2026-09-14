using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LinuxFoundationTests
{
    [Fact]
    public void CompositorIdentityRoundTripsWithoutAnHwnd()
    {
        var target = new LinuxCaptureTarget(LinuxCaptureTargetKind.KdeWindow, "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}");
        var copy = JsonSerializer.Deserialize<LinuxCaptureTarget>(JsonSerializer.Serialize(target));
        Assert.Equal(target, copy);
        Assert.Equal("kde-window:{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", copy!.ToRecorderSource());
        Assert.Equal("kde-output:DP-1", new LinuxCaptureTarget(LinuxCaptureTargetKind.KdeOutput, "DP-1").ToRecorderSource());
    }

    [Theory]
    [InlineData(LinuxCaptureTargetKind.KdeWindow, "12345")]
    [InlineData(LinuxCaptureTargetKind.KdeOutput, "")]
    [InlineData(LinuxCaptureTargetKind.KdeOutput, "DP-1\n-w screen")]
    [InlineData((LinuxCaptureTargetKind)99, "DP-1")]
    public void RejectsInvalidCompositorTargets(LinuxCaptureTargetKind kind, string id) =>
        Assert.Throws<ArgumentException>(() => new LinuxCaptureTarget(kind, id).ToRecorderSource());

    [Fact]
    public async Task WorkerRejectsOlderConfigurationProtocol()
    {
        using var stream = new MemoryStream();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Version = 11, Type = "start", RequestId = Guid.NewGuid(), Payload = new { } });
        stream.Write(BitConverter.GetBytes(payload.Length)); stream.Write(payload); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => CaptureWorkerPipe.ReadAsync(stream, default));
    }

    [Fact]
    public async Task LinuxCannotDownloadWindowsInstaller()
    {
        Assert.Null(await AppUpdateService.CheckAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => DevUpdateService.StageLatestAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => AppUpdateService.DownloadAndRestartAsync(null!));
    }

    [Fact]
    public async Task UnavailableRecorderNeverClaimsRecordingOrSavesAClip()
    {
        using var buffer = new LinuxReplayBuffer();
        Assert.False(buffer.GetReadiness().Ready);
        Assert.Equal(ReplayBackendCapabilities.None, buffer.GetReadiness().Capabilities);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => buffer.StartAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => buffer.SaveReplayAsync("/unused"));
        Assert.False(buffer.IsRecording);
        Assert.Equal(ReplayCaptureState.Failed, buffer.GetHealthSnapshot().State);
    }

    [Fact]
    public void UnavailableEditorFailsBeforeLoadingWindowsVideoOutput() =>
        Assert.Throws<PlatformNotSupportedException>(() => new PlaybackSession());

    [Fact]
    public void ActivationReachesExistingOwner()
    {
        using var activated = new ManualResetEventSlim();
        using var owner = new LinuxSingleInstance(() => activated.Set());
        Assert.True(owner.IsOwner);
        using var second = new LinuxSingleInstance(() => throw new InvalidOperationException());
        Assert.False(second.IsOwner);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void DesktopExecEscapesFieldCodesAndRejectsLineInjection()
    {
        Assert.Equal("\"/tmp/a b/100%%/ClypDat\"", LinuxDesktopEntry.QuoteExec("/tmp/a b/100%/ClypDat"));
        Assert.Throws<ArgumentException>(() => LinuxDesktopEntry.QuoteExec("/tmp/app\nHidden=true"));
        Assert.Throws<ArgumentException>(() => LinuxDesktopEntry.QuoteExec("app"));
    }
}
