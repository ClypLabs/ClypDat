using System.Reflection;
using System.Runtime.CompilerServices;
using ClypDat.App.Converters;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SystemAudioSettingsTests
{
    [Fact]
    public void TogglePersistsRestoresAppSelectionsAndRestartsRecorder() => AvaloniaTestThread.Run(() =>
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-SystemAudioTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        try
        {
            var settings = new AppSettings { AdditionalAudioProcesses = new() { ["Discord"] = 75 }, GameAudioVolumePercent = 80 };
            var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            typeof(MainWindowViewModel).GetField("<Settings>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, settings);
            typeof(MainWindowViewModel).GetField("_activeGameDetection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, GameDetection.None);
            typeof(MainWindowViewModel).GetField("_selectedReplayDurationPreset", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, MainWindowViewModel.DurationPresets.Single(p => p.Seconds == settings.ReplayDurationSeconds));
            var initial = vm.CreateReplayConfig();
            Assert.False(initial.SystemAudioEnabled);
            Assert.Equal(100, initial.SystemAudioVolumePercent);
            vm.SystemAudioEnabled = true;
            vm.SystemAudioVolumePercent = 125;
            var enabled = vm.CreateReplayConfig();
            Assert.True(enabled.SystemAudioEnabled);
            Assert.Equal(125, enabled.SystemAudioVolumePercent);
            Assert.NotEqual(ReplayBufferConfigIdentity.Serialize(initial), ReplayBufferConfigIdentity.Serialize(enabled));
            var differs = typeof(MainWindow).GetMethod("RuntimeSettingsDiffer", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)differs.Invoke(null, [initial, enabled])!);
            Assert.True((bool)differs.Invoke(null, [enabled, enabled with { SystemAudioVolumePercent = 50 }])!);
            var loaded = AppSettingsStore.Load();
            Assert.True(loaded.SystemAudioEnabled);
            Assert.Equal(125, loaded.SystemAudioVolumePercent);
            Assert.Equal(75, loaded.AdditionalAudioProcesses["Discord"]);
            vm.SystemAudioEnabled = false;
            Assert.False(vm.CreateReplayConfig().SystemAudioEnabled);
            Assert.Equal(initial.AdditionalAudioProcesses, vm.CreateReplayConfig().AdditionalAudioProcesses);
            Assert.Equal(80, settings.GameAudioVolumePercent);
            Assert.True(SettingsSearchMatchConverter.MatchesSection("All System Audio", "Audio"));
            Assert.True(SettingsSearchMatchConverter.MatchesSection("Full System Audio", "Audio"));
        }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, TimeSpan.FromSeconds(30), "System audio settings test timed out.");
}
