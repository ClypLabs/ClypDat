using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClypDat.App.Converters;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OscSettingsIntegrationTests
{
    [Fact]
    public void UpdatesGlobalAndEffectiveActiveOverrideRefreshesBothViewsAndPersistsOnce() => Isolated(() =>
    {
        var settings = Settings();
        var profile = new CustomGameProfile { ReplayDurationSeconds = 120, Groups = ["Replay"] };
        var other = new CustomGameProfile { ReplayDurationSeconds = 300, Groups = ["Replay"] };
        settings.CustomGameSettings["fixture.exe"] = profile;
        settings.CustomGameSettings["other.exe"] = other;
        var vm = ViewModel(settings);
        var tab = Tab(profile);
        vm.CustomGameTabs.Add(tab);
        Assert.Equal(120, tab.SelectedDurationPreset!.Seconds); // Prime its cached selection.
        var globalChanges = new List<string?>();
        var gameChanges = new List<string?>();
        vm.PropertyChanged += (_, e) => globalChanges.Add(e.PropertyName);
        tab.PropertyChanged += (_, e) => gameChanges.Add(e.PropertyName);
        var saves = 0;
        vm.RecordingSettingsSaved += () => saves++;
        var initial = vm.CreateReplayConfig();

        Assert.True(vm.ApplyOscReplayDuration(30));
        Assert.Equal(1, saves);
        Assert.Equal(30, settings.ReplayDurationSeconds);
        Assert.Equal(30, profile.ReplayDurationSeconds);
        Assert.Equal(300, other.ReplayDurationSeconds);
        Assert.Equal(["Replay"], profile.Groups);
        Assert.Equal(30, vm.SelectedReplayDurationPreset!.Seconds);
        Assert.Equal(30, tab.SelectedDurationPreset!.Seconds);
        Assert.Contains(nameof(MainWindowViewModel.SelectedReplayDurationPreset), globalChanges);
        Assert.Contains(nameof(MainWindowViewModel.ReplayDurationSummary), globalChanges);
        Assert.Contains(nameof(CustomGameTabViewModel.SelectedDurationPreset), gameChanges);
        Assert.Contains(nameof(CustomGameTabViewModel.ReplayDurationSeconds), gameChanges);
        var persisted = AppSettingsStore.Load();
        Assert.Equal(30, persisted.ReplayDurationSeconds);
        Assert.Equal(30, persisted.CustomGameSettings["fixture.exe"].ReplayDurationSeconds);
        Assert.True(NeedsRestart(initial, vm.CreateReplayConfig()));

        var savedJson = File.ReadAllText(AppSettingsStore.SettingsPath);
        globalChanges.Clear(); gameChanges.Clear();
        Assert.False(vm.ApplyOscReplayDuration(30));
        Assert.Equal(1, saves);
        Assert.Empty(globalChanges); Assert.Empty(gameChanges);
        Assert.Equal(savedJson, File.ReadAllText(AppSettingsStore.SettingsPath));
    });

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    public void ChangesOnlyExistingEffectiveReplayOverride(bool desktop, bool replayGroup, bool automaticGame, bool changesGame) => Isolated(() =>
    {
        var settings = Settings();
        settings.ReplayCaptureSource = desktop ? "Desktop" : "Game";
        settings.ReplayAutoSwitchToGameCapture = automaticGame;
        var profile = new CustomGameProfile { ReplayDurationSeconds = 120, Groups = replayGroup ? ["Replay"] : ["Quality"] };
        settings.CustomGameSettings["fixture.exe"] = profile;
        var vm = ViewModel(settings);
        Assert.True(vm.ApplyOscReplayDuration(30));
        Assert.Equal(30, settings.ReplayDurationSeconds);
        Assert.Equal(changesGame ? 30 : 120, profile.ReplayDurationSeconds);
        Assert.Equal(replayGroup ? ["Replay"] : ["Quality"], profile.Groups);
        Assert.Single(settings.CustomGameSettings);
    });

    [Fact]
    public void MissingProfileAndUndetectedGameNeverCreateOrEnableOverride() => Isolated(() =>
    {
        var settings = Settings();
        var vm = ViewModel(settings);
        Assert.True(vm.ApplyOscReplayDuration(30));
        Assert.Empty(settings.CustomGameSettings);
        var profile = new CustomGameProfile { ReplayDurationSeconds = 120, Groups = ["Replay"] };
        settings.CustomGameSettings["fixture.exe"] = profile;
        Field(vm, "_activeGameDetection", GameDetection.None);
        Assert.True(vm.ApplyOscReplayDuration(180));
        Assert.Equal(120, profile.ReplayDurationSeconds);
    });

    [Fact]
    public void ActiveAliasUsesSameEffectiveProfileAsRecorder() => Isolated(() =>
    {
        var settings = Settings();
        settings.GameCaptureOverrides = [new() { ExecutableName = "fixture.exe", DisplayName = "Fixture" },
            new() { ExecutableName = "steam-fixture", DisplayName = "Fixture" }];
        settings.CustomGameSettings["steam-fixture"] = new() { ReplayDurationSeconds = 120, Groups = ["Replay"] };
        var vm = ViewModel(settings);
        Assert.Equal(120, vm.CreateReplayConfig().DurationSeconds);
        Assert.True(vm.ApplyOscReplayDuration(30));
        Assert.Equal(30, settings.CustomGameSettings["steam-fixture"].ReplayDurationSeconds);
        Assert.Equal(30, vm.CreateReplayConfig().DurationSeconds);
        Assert.Single(settings.CustomGameSettings);
    });

    [Fact]
    public void GlobalChangeWithAlreadyMatchingOverrideSavesWithoutRestart() => Isolated(() =>
    {
        var settings = Settings();
        settings.CustomGameSettings["fixture.exe"] = new() { ReplayDurationSeconds = 30, Groups = ["Replay"] };
        var vm = ViewModel(settings);
        var initial = vm.CreateReplayConfig();
        var restarts = 0;
        vm.RecordingSettingsSaved += () => { if (NeedsRestart(initial, vm.CreateReplayConfig())) restarts++; };
        Assert.True(vm.ApplyOscReplayDuration(30));
        Assert.Equal(0, restarts);
        Assert.Equal(30, AppSettingsStore.Load().ReplayDurationSeconds);
    });

    [Fact]
    public void DurationThenClipInBundleRejectsUntilRestartAndNewReception() => Isolated(() =>
    {
        var vm = ViewModel(Settings());
        using var buffer = new FakeReplayBuffer();
        var original = new OscCaptureSnapshot(buffer, vm.CreateReplayConfig(), 1, 1, "Fixture", null);
        var current = original;
        vm.RecordingSettingsSaved += () => current = current with { Config = vm.CreateReplayConfig(), Generation = 2, Refusal = "capture is reconfiguring" };
        Assert.True(OscPacketDecoder.TryDecode(OscTestPackets.Bundle(OscTestPackets.Duration(30), OscTestPackets.Clip()), out var commands));
        var packet = new OscReceivedPacket(commands, DateTime.UtcNow, CancellationToken.None, original);
        var refused = new List<string>();
        void Clip(OscReceivedPacket received)
        {
            var request = new OscClipRequest((OscCaptureSnapshot)received.ReceiptContext!, received.ReceivedUtc, received.ListenerLifetime);
            if (OscClipPolicy.Validate(request, current) is { } reason) refused.Add(reason);
            else { buffer.Submissions++; current = current with { Refusal = "another operation is busy" }; }
        }
        packet.Dispatch(seconds => vm.ApplyOscReplayDuration(seconds), Clip);
        Assert.Equal(30, vm.Settings.ReplayDurationSeconds);
        Assert.Equal(0, buffer.Submissions);
        Assert.Single(refused);
        current = current with { Refusal = null };
        new OscReceivedPacket([new(OscCommandKind.Clip), new(OscCommandKind.Clip)], DateTime.UtcNow,
            CancellationToken.None, current).Dispatch(_ => { }, Clip);
        Assert.Equal(1, buffer.Submissions); // The second clip refuses rather than queues.
        Assert.Equal(2, refused.Count);
    });

    [Theory]
    [InlineData("quitting")] [InlineData("busy")] [InlineData("reconfiguring")]
    [InlineData("arming")] [InlineData("suspended")] [InlineData("unavailable")]
    public void ClipPolicyRefusesEveryUnavailableState(string state)
    {
        Assert.NotNull(OscClipPolicy.Refusal(state == "quitting", state == "busy", state == "reconfiguring",
            state == "arming", state == "suspended", state != "unavailable"));
        Assert.Null(OscClipPolicy.Refusal(false, false, false, false, false, true));
    }

    [Fact]
    public void ClipEndpointRemainsReceptionTimeAndIdentityIsRechecked()
    {
        using var buffer = new FakeReplayBuffer();
        using var other = new FakeReplayBuffer();
        using var lifetime = new CancellationTokenSource();
        var config = TestReplayConfiguration.Create("", "MKV") with { DurationSeconds = 120 };
        var snapshot = new OscCaptureSnapshot(buffer, config, 10, 20, "Fixture", null);
        var received = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var request = new OscClipRequest(snapshot, received, lifetime.Token);
        Assert.Equal(received, request.Window.EndUtc);
        Assert.Equal(received.AddSeconds(-120), request.Window.StartUtc);
        Assert.Null(OscClipPolicy.Validate(request, snapshot));
        Assert.NotNull(OscClipPolicy.Validate(request, snapshot with { Buffer = other }));
        Assert.NotNull(OscClipPolicy.Validate(request, snapshot with { Generation = 11 }));
        Assert.NotNull(OscClipPolicy.Validate(request, snapshot with { WorkerGeneration = 21 }));
        Assert.NotNull(OscClipPolicy.Validate(request, snapshot with { Config = config with { DurationSeconds = 30 } }));
        Assert.NotNull(OscClipPolicy.Validate(request, null));
        lifetime.Cancel();
        Assert.NotNull(OscClipPolicy.Validate(request, snapshot));
    }

    [Theory]
    [InlineData("OSC")] [InlineData("Open Sound Control")] [InlineData("Network controls")]
    [InlineData("UDP")] [InlineData("Listener status")] [InlineData("Stream Deck")]
    public void OscSearchFindsAdvanced(string query)
    {
        // OSC moved from Replay Buffer to Advanced with the advanced recording controls.
        Assert.True(SettingsSearchMatchConverter.MatchesSection(query, "Advanced"));
        Assert.False(SettingsSearchMatchConverter.MatchesSection(query, "Replay Buffer"));
    }

    [Fact]
    public void OscDefaultsAndSavedSettingsLoadWithoutChangingOtherSettings() => Isolated(() =>
    {
        var defaults = AppSettingsStore.Load();
        Assert.False(defaults.OscEnabled);
        Assert.Equal(9001, defaults.OscPort);
        defaults.OscEnabled = true; defaults.OscPort = 9876; defaults.SaveReplayHotkey = "Alt+F10";
        Assert.True(AppSettingsStore.Save(defaults));
        var loaded = AppSettingsStore.Load();
        Assert.True(loaded.OscEnabled);
        Assert.Equal(9876, loaded.OscPort);
        Assert.Equal("Alt+F10", loaded.SaveReplayHotkey);
        defaults.OscPort = 65536;
        Assert.True(AppSettingsStore.Save(defaults));
        Assert.Equal(9001, AppSettingsStore.Load().OscPort);
    });

    private static AppSettings Settings() => new() { ReplayDurationSeconds = 60, ReplayBitrateDefault15Applied = true,
        ReplayH264DefaultApplied = true, SettingsSchemaVersion = AppSettingsMigrations.CurrentSchemaVersion };

    private static MainWindowViewModel ViewModel(AppSettings settings)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Field(vm, "<Settings>k__BackingField", settings);
        Field(vm, "<CustomGameTabs>k__BackingField", new ObservableCollection<CustomGameTabViewModel>());
        Field(vm, "_activeGameDetection", new GameDetection("Fixture", "fixture.exe", "", "", 0, 123, true, DetectionKey: "fixture.exe"));
        Field(vm, "_selectedReplayDurationPreset", MainWindowViewModel.DurationPresets.Single(p => p.Seconds == settings.ReplayDurationSeconds));
        return vm;
    }

    private static CustomGameTabViewModel Tab(CustomGameProfile profile)
    {
        var tab = (CustomGameTabViewModel)RuntimeHelpers.GetUninitializedObject(typeof(CustomGameTabViewModel));
        Field(tab, "<Profile>k__BackingField", profile);
        return tab;
    }

    private static bool NeedsRestart(ReplayBufferConfig before, ReplayBufferConfig after) =>
        (bool)typeof(MainWindow).GetMethod("RuntimeSettingsDiffer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [before, after])!;

    private static void Field(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    internal static void Isolated(Action action) => AvaloniaTestThread.Run(() =>
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-OscTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        Directory.CreateDirectory(root);
        try { action(); }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, TimeSpan.FromSeconds(30), "OSC settings integration test timed out.");

    private sealed class FakeReplayBuffer : IReplayBuffer
    {
        public int Submissions;
        public bool IsRecording => true;
        public TimeSpan Duration => TimeSpan.FromSeconds(120);
        public bool LastSaveVideoWasFrozen => false;
        public event EventHandler? RecordingStopped { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null,
            ReplayClipWindow? clipWindow = null, string? gameDisplayNameOverride = null, Guid? saveId = null) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
