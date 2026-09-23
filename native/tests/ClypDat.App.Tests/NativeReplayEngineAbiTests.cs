using System.Runtime.InteropServices;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeReplayEngineAbiTests
{
    [Fact]
    public void Structures_HaveStableVersionedX64Layout()
    {
        Assert.Equal(8, Marshal.SizeOf<NativeReplayEngineAbi.Header>());
        Assert.Equal(56, Marshal.SizeOf<NativeReplayEngineAbi.EngineConfig>());
        Assert.Equal(112, Marshal.SizeOf<NativeReplayEngineAbi.EngineHealth>());
        Assert.Equal(32, Marshal.SizeOf<NativeReplayEngineAbi.AbiInfo>());
        Assert.Equal(24, Marshal.SizeOf<NativeReplayEngineAbi.SaveRequest>());
        Assert.Equal(56, Marshal.SizeOf<NativeReplayEngineAbi.SaveResult>());
        Assert.Equal(40, Marshal.OffsetOf<NativeReplayEngineAbi.SaveResult>("TemporaryVideoPath").ToInt32());
        Assert.Equal(NativeReplayEngineAbi.Version, NativeReplayEngineAbi.Header.Create<NativeReplayEngineAbi.EngineHealth>().AbiVersion);
    }

    [Fact]
    public void AbiNegotiation_RejectsVersionAndLayoutMismatch()
    {
        var info = new NativeReplayEngineAbi.AbiInfo
        {
            Header = NativeReplayEngineAbi.Header.Create<NativeReplayEngineAbi.AbiInfo>(),
            EngineVersion = NativeReplayEngineAbi.EngineVersion,
            PointerSize = 8,
            ConfigSize = 56,
            HealthSize = 112,
            SaveRequestSize = 24,
            SaveResultSize = 56
        };
        Assert.True(info.IsCompatible);
        info.Header.AbiVersion = 2;
        Assert.False(info.IsCompatible);
        info.Header.AbiVersion = NativeReplayEngineAbi.Version;
        info.SaveResultSize = 48;
        Assert.False(info.IsCompatible);
    }

    [Fact]
    public void HealthMapping_ReportsConfiguredAndActiveCadenceSeparately()
    {
        var health = NativeReplayEngine.MapHealth(new NativeReplayEngineAbi.EngineHealth
        {
            State = NativeReplayEngineAbi.EngineState.Running,
            SelectedFps = 90,
            ActiveFps = 60,
            CaptureRoute = NativeReplayEngineAbi.CaptureRoute.Dxgi,
            QueueCapacity = 8,
            InputFps = 194.5,
            FreshFps = 89.8,
            OutputFps = 89.9
        });

        Assert.Equal(90, health.ConfiguredFrameRate);
        Assert.Equal(60, health.TargetFrameRate);
        Assert.Equal("DXGI", health.CaptureMode);
        Assert.Equal(89.9, health.OutputFrameRate, 1);
        Assert.Equal("GPU resident", health.EncoderInputPath);
    }
}
