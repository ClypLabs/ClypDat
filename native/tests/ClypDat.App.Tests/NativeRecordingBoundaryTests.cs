using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeRecordingBoundaryTests
{
    [Fact]
    public void LocalReplayFactoryCreatesTheNativeRecordingAdapter()
    {
        var output = Path.Combine(Path.GetTempPath(), "clypdat-factory-test");
        var recording = ReplayBufferFactory.CreateLocal(() => TestReplayConfiguration.Create(output, "MKV"));
        Assert.IsType<NativeRecordingAdapter>(recording);
        recording.Dispose();
    }

    [Fact]
    public void GeneratedProductionNativeSessionsSaveDecodeAndSeekAcrossAllPacingModes()
    {
        var root = Environment.GetEnvironmentVariable("CLYPDAT_NATIVE_COMPARISON");
        if (string.IsNullOrWhiteSpace(root)) return;
        var roundsText = Environment.GetEnvironmentVariable("CLYPDAT_NATIVE_COMPARISON_ROUNDS");
        var rounds = string.IsNullOrWhiteSpace(roundsText) ? 1 : int.Parse(roundsText);
        Assert.Equal(0, NativeRecordingVerification.Run(root, rounds));
    }

    [Fact]
    public void BundledProductionLibraryNegotiatesManagedStructureSizes()
    {
        var contract = NativeRecorderSession.ReadContract();
        Assert.Equal(8u, contract.PointerSize);
        Assert.Equal(Marshal.SizeOf<NativeRecorderSession.Configuration>(), (int)contract.ConfigSize);
        Assert.Equal(Marshal.SizeOf<NativeRecorderSession.Health>(), (int)contract.HealthSize);
        Assert.Equal(Marshal.SizeOf<NativeRecorderSession.Overlay>(), (int)contract.OverlaySize);
    }
    [Fact]
    public void EveryIncompleteCapabilityMaskIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => NativeRecorderSession.RequireCapabilities(0));
        foreach (var flag in new ulong[] { 1, 2, 4, 8, 16, 32 })
            Assert.Throws<NotSupportedException>(() => NativeRecorderSession.RequireCapabilities(63 & ~flag));
        NativeRecorderSession.RequireCapabilities(63);
    }
    [Fact]
    public void IndependentPreviewHandleDoesNotOpenADeviceUntilStarted()
    {
        for (var iteration = 0; iteration < 5; ++iteration)
        {
            using var preview = NativeCameraPreview.Create();
            var frame = preview.Copy(new byte[CameraPreviewService.FrameBytes]);
            Assert.Equal(0u, frame.RequiredBytes); Assert.Equal(0u, frame.Running);
            Assert.Empty(preview.Error());
        }
    }
    [Fact]
    public void KeyboardArtworkOnlyChangesOnEventsAndRetainsPriorPixels()
    {
        using var service = new RecordingKeyboardArtworkService();
        var settings = OverlayCaptureSettings.None with { KeyboardLayout = "WASD" };
        var first = service.Update(settings, [], 0, 1920, 1080, 100)!;
        var retained = first.PremultipliedBgra.ToArray();
        Assert.Null(service.Update(settings, [], 0, 1920, 1080, 200));
        var second = service.Update(settings, [new(0x11, false, false)], 1, 1920, 1080, 300)!;
        Assert.Equal(retained, first.PremultipliedBgra);
        Assert.NotSame(first.PremultipliedBgra, second.PremultipliedBgra);
        Assert.True(second.Revision > first.Revision);
    }
    [Fact]
    public void BoundaryContainsNoManagedFrameOrPcmCallback()
    {
        foreach (var type in new[] { typeof(NativeRecorderSession.Configuration), typeof(NativeRecorderSession.Artwork), typeof(NativeRecorderSession.SaveRequest) })
            Assert.DoesNotContain(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.All(typeof(NativeRecordingAdapter).GetInterfaces(), contract => Assert.DoesNotContain("CaptureCallback", contract.Name));
    }
    [Fact]
    public void InputAtMappingBoundarySurvivesWithoutNewCheckpoint()
    {
        var key = new InputPhysicalKey(0x11, false, false);
        var input = new InputCaptureIndex(2, null, [new(1, key, true, "Key"), new(1.5, key, false, "Key")], [new(0, [])]);
        var mapped = NativeRecordingPublication.RebaseInput(input, 10_000_000,
            [new(10_000_000, 1_000_000, 0), new(11_000_000, 1_000_000, 1_000_000)]);
        var roundTrip = JsonSerializer.Deserialize<InputCaptureIndex>(JsonSerializer.Serialize(mapped));
        Assert.Contains("KeyW", ClipInputIndex.PressedAt(roundTrip, 1.2));
        Assert.Empty(ClipInputIndex.PressedAt(roundTrip, 1.8));
    }
    [Fact]
    public void AcquisitionGapRestoresHeldStateAtNextOutputRange()
    {
        var key = new InputPhysicalKey(0x11, false, false);
        var input = new InputCaptureIndex(2, null, [new(3, key, false, "Key")], [new(0, [key])]);
        var mapped = NativeRecordingPublication.RebaseInput(input, 0,
            [new(0, 1_000_000, 0), new(5_000_000, 1_000_000, 1_000_000)]);
        Assert.Contains("KeyW", ClipInputIndex.PressedAt(mapped, .5));
        Assert.Empty(ClipInputIndex.PressedAt(mapped, 1.5));
    }
    [Fact]
    public void ImmutableNativeResultPublishesReaderCompatibleV2AndV7()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "native-boundary-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "Clips", "Game", "fixture.mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, []);
            var configuration = new ReplayBufferConfig(60, 1080, 60, 0, 0, 1920, 1080, "", "", [], [], "", [], "Game", "game.exe", "", "", LibraryFolder: root);
            var settings = OverlayCaptureSettings.None with { KeyboardLayout = "WASD" };
            var result = JsonSerializer.SerializeToElement(new
            {
                audioMappings = new[] { new { sourceStartUs = 20_000_000L, durationUs = 2_000_000L, outputStartUs = 0L } },
                overlayMappings = new[] { new { sourceStartUs = 10_000_000L, durationUs = 2_000_000L, outputStartUs = 0L } },
                inputStartUs = 10_000_000L,
                input = new InputCaptureIndex(2, null, [new(.5, new(0x11, false, false), true, "Key")], [new(0, [])]),
                settings = new[] { new { atUs = 10_000_000L, revision = 1, value = settings } }, camera = Array.Empty<object>(), burnedCamera = false, burnedKeyboard = false
            });
            NativeRecordingPublication.Publish(configuration, output, "Game", "Fixture", result, 2_000_000, 10_000_000);
            var info = ClipInfoSidecar.Load(root, output);
            Assert.Equal(7, info!.OverlayManifest!.Version);
            var index = ClipInputIndex.Load(root, info.OverlayManifest.Peripherals);
            Assert.Equal(2, index!.Version); Assert.Contains("KeyW", ClipInputIndex.PressedAt(index, 1));
            var source = SpotifySourceWindow.Load(root, output);
            Assert.Equal(20, source!.StartSeconds); Assert.Equal(1, source.MediaSeconds(21));
            Assert.True(double.IsNaN(source.MediaSeconds(11)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void PinnedCameraTailUsesAcquisitionMappingAndOriginalSourceOffset()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "native-camera-publication-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "Clips", "Game", "fixture.mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, []);
            var sourceCamera = Path.Combine(root, "pinned-fragments.mp4"); File.WriteAllBytes(sourceCamera, [1, 2, 3]);
            var configuration = new ReplayBufferConfig(60, 1080, 60, 0, 0, 1920, 1080, "", "", [], [], "", [], "Game", "game.exe", "", "", LibraryFolder: root);
            var settings = OverlayCaptureSettings.None with { Camera = new("device", "Camera") };
            var result = JsonSerializer.SerializeToElement(new
            {
                audioMappings = new[] { new { sourceStartUs = 20_000_000L, durationUs = 2_000_000L, outputStartUs = 0L } },
                overlayMappings = new[]
                {
                    new { sourceStartUs = 10_000_000L, durationUs = 1_000_000L, outputStartUs = 0L },
                    new { sourceStartUs = 12_000_000L, durationUs = 1_000_000L, outputStartUs = 1_000_000L }
                },
                inputStartUs = 10_000_000L, input = new InputCaptureIndex(2, null, [], [new(0, [])]),
                settings = new[] { new { atUs = 9_000_000L, revision = 1, value = settings } },
                camera = new[] { new { generation = 1, startUs = 9_500_000L, endUs = 12_500_000L, path = sourceCamera, completed = true } },
                burnedCamera = false, burnedKeyboard = false
            });
            NativeRecordingPublication.Publish(configuration, output, "Game", "Fixture", result, 2_000_000, 10_000_000);
            var assets = ClipInfoSidecar.Load(root, output)!.OverlayManifest!.Camera!.Assets!;
            Assert.Equal(2, assets.Count);
            Assert.Equal((0d, 1d, .5d), (assets[0].StartSeconds, assets[0].EndSeconds, assets[0].SourceOffsetSeconds));
            Assert.Equal((1d, 1.5d, 2.5d), (assets[1].StartSeconds, assets[1].EndSeconds, assets[1].SourceOffsetSeconds));
            Assert.All(assets, asset => Assert.Equal(1d, asset.PlaybackRate));
            Assert.Equal(assets[0].AssetPath, assets[1].AssetPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
