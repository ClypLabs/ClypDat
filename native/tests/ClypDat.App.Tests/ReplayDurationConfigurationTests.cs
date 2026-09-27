using System.Reflection;
using System.Runtime.CompilerServices;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayDurationConfigurationTests
{
    private static void Field(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    // Use real config construction/setters without starting audio, accounts,
    // game detection or capture from the main view model constructor.
    private static MainWindowViewModel ViewModel(AppSettings settings)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Field(vm, "<Settings>k__BackingField", settings);
        Field(vm, "_activeGameDetection", new GameDetection("Fixture game", "fixture.exe", "", "", 0, 123, true, DetectionKey: "fixture.exe"));
        Field(vm, "_selectedReplayDurationPreset", MainWindowViewModel.DurationPresets.Single(p => p.Seconds == settings.ReplayDurationSeconds));
        return vm;
    }

    private static void Isolated(Action action) => AvaloniaTestThread.Run(() =>
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-ReplayDurationTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        try { action(); }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, TimeSpan.FromSeconds(30), "Replay duration configuration test timed out.");

    [Theory]
    [InlineData(60, null, null, false, 60)]
    [InlineData(60, 120, "Replay", false, 120)]
    [InlineData(120, 30, "Replay", false, 30)]
    [InlineData(60, 120, "Quality", false, 60)]
    [InlineData(60, 120, "Replay", true, 60)]
    public void EffectiveDurationReachesActualRecorderConfig(int global, int? game, string? group, bool desktop, int expected) => Isolated(() =>
    {
        var settings = new AppSettings { ReplayDurationSeconds = global, ReplayCaptureSource = desktop ? "Desktop" : "Game", ReplayAutoSwitchToGameCapture = false };
        if (game is { } duration)
            settings.CustomGameSettings["fixture.exe"] = new CustomGameProfile { ReplayDurationSeconds = duration, Groups = [group!] };
        var config = ViewModel(settings).CreateReplayConfig();
        Assert.Equal(expected, config.DurationSeconds);
        Assert.Equal(desktop ? "Desktop" : "Game", config.CaptureSource);
    });

    [Fact]
    public void GlobalPresetChangeDoesNotOverrideActiveGameReplayGroup() => Isolated(() =>
    {
        var settings = new AppSettings { ReplayDurationSeconds = 60 };
        settings.CustomGameSettings["fixture.exe"] = new CustomGameProfile { ReplayDurationSeconds = 120, Groups = [CustomGameSettingsResolver.ReplayGroup] };
        var vm = ViewModel(settings);
        var before = vm.CreateReplayConfig();
        vm.SelectedReplayDurationPreset = MainWindowViewModel.DurationPresets.Single(p => p.Seconds == 30);
        Assert.Equal(30, settings.ReplayDurationSeconds);
        Assert.Equal(120, vm.CreateReplayConfig().DurationSeconds);
        Assert.Equal(ReplayBufferConfigIdentity.Serialize(before), ReplayBufferConfigIdentity.Serialize(vm.CreateReplayConfig()));
    });

    [Fact]
    public void ActiveOverrideChangeAndRemovalUseExistingRestartAndWorkerIdentity() => Isolated(() =>
    {
        var settings = new AppSettings { ReplayDurationSeconds = 60 };
        var profile = new CustomGameProfile { ReplayDurationSeconds = 120, Groups = [CustomGameSettingsResolver.ReplayGroup] };
        settings.CustomGameSettings["fixture.exe"] = profile;
        var vm = ViewModel(settings);
        var initial = vm.CreateReplayConfig();
        var changes = new List<CustomGameSettingChange>();
        var game = (CustomGameTabViewModel)RuntimeHelpers.GetUninitializedObject(typeof(CustomGameTabViewModel));
        Field(game, "<Profile>k__BackingField", profile);
        Field(game, "_settings", settings);
        Field(game, "_save", (Action)vm.SaveSettings);
        Field(game, "_settingChanged", (Action<CustomGameSettingChange>)changes.Add);
        game.SelectedDurationPreset = MainWindowViewModel.DurationPresets.Single(p => p.Seconds == 30);
        var changed = vm.CreateReplayConfig();
        Assert.Equal(30, changed.DurationSeconds);
        Assert.NotEmpty(changes);
        AssertRestart(initial, changed);
        game.HasReplay = false;
        var removed = vm.CreateReplayConfig();
        Assert.Equal(60, removed.DurationSeconds);
        Assert.Contains(CustomGameSettingChange.Group, changes);
        AssertRestart(changed, removed);
    });

    private static void AssertRestart(ReplayBufferConfig before, ReplayBufferConfig after)
    {
        Assert.NotEqual(ReplayBufferConfigIdentity.Serialize(before), ReplayBufferConfigIdentity.Serialize(after));
        var method = typeof(MainWindow).GetMethod("RuntimeSettingsDiffer", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)method.Invoke(null, [before, after])!);
    }
}
