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
