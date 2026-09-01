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
        Assert.Equal(NativeReplayEngineAbi.Version, NativeReplayEngineAbi.Header.Create<NativeReplayEngineAbi.EngineHealth>().AbiVersion);
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
