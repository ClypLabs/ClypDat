using ClypDat.App.Services;
using ClypDat.Core.Settings;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DefaultMicrophoneTests
{
    [Fact]
    public void WatcherRootsCallbackUntilSuccessfulUnregistration()
    {
        var registration = new FakeRegistration();
        var watcher = new DefaultMicrophoneWatcher(() => { }, registration);

        Assert.True(registration.Registered);
        Assert.True(watcher.CallbackRooted);
        watcher.Dispose();

        Assert.Equal(1, registration.UnregisterCalls);
        Assert.True(registration.Disposed);
        Assert.False(watcher.CallbackRooted);
    }

    [Fact]
    public void FailedUnregistrationKeepsInertCallbackRooted()
    {
        var registration = new FakeRegistration { FailUnregister = true };
        var changes = 0;
        var watcher = new DefaultMicrophoneWatcher(() => Interlocked.Increment(ref changes), registration);

        watcher.Dispose();
        registration.Callback!.OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, "microphone");

        Assert.Equal(1, registration.UnregisterCalls);
        Assert.False(registration.Disposed);
        Assert.True(watcher.CallbackRooted);
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public void FailedRegistrationDisposesAdapterAndDoesNotRetainCallback()
    {
        var registration = new FakeRegistration { FailRegister = true };

        Assert.Throws<InvalidOperationException>(() => new DefaultMicrophoneWatcher(() => { }, registration));
        Assert.True(registration.Disposed);
    }
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

    private sealed class FakeRegistration : IDefaultMicrophoneNotificationRegistration
    {
        public IMMNotificationClient? Callback { get; private set; }
        public bool Registered { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailRegister { get; init; }
        public bool FailUnregister { get; init; }
        public int UnregisterCalls { get; private set; }

        public void Register(IMMNotificationClient callback)
        {
            Callback = callback;
            if (FailRegister) throw new InvalidOperationException("register failed");
            Registered = true;
        }

        public void Unregister(IMMNotificationClient callback)
        {
            UnregisterCalls++;
            if (FailUnregister) throw new InvalidOperationException("unregister failed");
            Assert.Same(Callback, callback);
        }

        public void Dispose() => Disposed = true;
    }
}
