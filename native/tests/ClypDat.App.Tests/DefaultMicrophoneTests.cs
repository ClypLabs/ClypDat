using ClypDat.App.Services;
using ClypDat.Core.Settings;
using NAudio.CoreAudioApi;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DefaultMicrophoneTests
{
    [Fact]
    public void DeviceSelectionDuringSnapshot_DoesNotPersistTransientNull()
    {
        var change = AudioDeviceSelectionChange.FromPicker(null, isApplyingSnapshot: true);

        Assert.False(change.ShouldPersist);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("physical-microphone-id")]
    public void DeviceSelectionOutsideSnapshot_PersistsSelection(string deviceId)
    {
        var change = AudioDeviceSelectionChange.FromPicker(
            new AudioDeviceOption(deviceId, "Microphone"), isApplyingSnapshot: false);

        Assert.True(change.ShouldPersist);
        Assert.Equal(deviceId, change.DeviceId);
    }

    [Fact]
    public void V6Migration_RestoresBlankMicrophoneToDefaultWithoutChangingMultiMicrophoneSelection()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 6,
            MicrophoneDeviceId = "  ",
            MultiMicrophoneEnabled = true,
            MicrophoneDeviceIds = ["microphone-a", "microphone-b"]
        };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal(AppSettingsMigrations.CurrentSchemaVersion, settings.SettingsSchemaVersion);
        Assert.Equal(AudioDeviceOption.DefaultDeviceId, settings.MicrophoneDeviceId);
        Assert.True(settings.MultiMicrophoneEnabled);
        Assert.Equal(["microphone-a", "microphone-b"], settings.MicrophoneDeviceIds);
    }

    [Fact]
    public void V6Migration_PreservesExplicitMicrophoneSelection()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 6, MicrophoneDeviceId = "physical-microphone-id" };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal("physical-microphone-id", settings.MicrophoneDeviceId);
    }

    [Fact]
    public void DefaultResolution_UsesMultimediaRole()
    {
        Assert.Equal(Role.Multimedia, DefaultMicrophone.Role);
    }

    [Theory]
    [InlineData(DataFlow.Capture, Role.Console, true)]
    [InlineData(DataFlow.Capture, Role.Multimedia, true)]
    [InlineData(DataFlow.Capture, Role.Communications, false)]
    [InlineData(DataFlow.Render, Role.Console, false)]
    [InlineData(DataFlow.Render, Role.Multimedia, false)]
    public void DefaultChangeWatcher_OnlyHandlesCaptureConsoleOrMultimedia(DataFlow flow, Role role, bool expected)
    {
        Assert.Equal(expected, DefaultMicrophoneWatcher.IsRelevantDefaultChange(flow, role));
    }
}
