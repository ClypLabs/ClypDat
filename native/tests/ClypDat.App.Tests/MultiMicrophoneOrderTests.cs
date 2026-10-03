using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class MultiMicrophoneOrderTests
{
    private static void Field(object target, string name, object? value) => typeof(MainWindowViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static readonly AudioDeviceOption Default = new(AudioDeviceOption.DefaultDeviceId, "Default - Mic A");
    private static readonly AudioDeviceOption MicA = new("id-a", "Mic A");
    private static readonly AudioDeviceOption MicB = new("id-b", "Mic B");

    // Constructor services (audio, accounts, capture) are skipped; the real
    // setters run against isolated settings with Mic A as the Windows default.
    private static MainWindowViewModel Create(AppSettings settings)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Field(vm, "<Settings>k__BackingField", settings);
        Field(vm, "<MicrophoneDevices>k__BackingField", new ObservableCollection<AudioDeviceOption> { Default, MicA, MicB });
        Field(vm, "<SelectedMicrophones>k__BackingField", new ObservableCollection<AudioDeviceOption>());
        Field(vm, "<MicrophoneTrackRows>k__BackingField", new ObservableCollection<MicrophoneTrackRow>());
        Field(vm, "<MicrophonesToAdd>k__BackingField", new ObservableCollection<AudioDeviceOption>());
        Field(vm, "_selectedMicrophoneDevice", Default);
        Field(vm, "_defaultMicrophoneName", "Mic A");
        var rebuild = typeof(MainWindowViewModel).GetMethod("RebuildMicrophoneRows", BindingFlags.Instance | BindingFlags.NonPublic)!;
        vm.SelectedMicrophones.CollectionChanged += (_, _) => rebuild.Invoke(vm, null);
        return vm;
    }

    [Fact]
    public void TurningOnMultipleMicrophonesKeepsTheCurrentMicAsMicrophoneOne()
    {
        AvaloniaTestThread.Run(() =>
        {
            var previous = AppDataPaths.ProductFolderName;
            AppDataPaths.ConfigureProductFolder("ClypDat-MultiMicTest-" + Guid.NewGuid().ToString("N"));
            var root = AppDataPaths.Root;
            try
            {
                var settings = new AppSettings { MicrophoneDeviceId = AudioDeviceOption.DefaultDeviceId };
                var vm = Create(settings);

                vm.MultiMicrophoneEnabled = true;

                Assert.Equal([AudioDeviceOption.DefaultDeviceId], settings.MicrophoneDeviceIds);
                Assert.Equal("Microphone 1", vm.MicrophoneTrackRows[0].Label);
                Assert.Equal(Default, vm.MicrophoneTrackRows[0].Device);
                // "Default" already resolves to Mic A, so only Mic B is offered.
                Assert.Equal([MicB], vm.MicrophonesToAdd);
                Assert.Equal(MicB, vm.MicrophoneToAdd);

                vm.AddSelectedMicrophone();

                Assert.Equal([AudioDeviceOption.DefaultDeviceId, "id-b"], settings.MicrophoneDeviceIds);
                Assert.Equal(("Microphone 2", MicB), (vm.MicrophoneTrackRows[1].Label, vm.MicrophoneTrackRows[1].Device));
                Assert.Empty(vm.MicrophonesToAdd);
                // Choosing what to add never rewrote the single-microphone setting.
                Assert.Equal(AudioDeviceOption.DefaultDeviceId, settings.MicrophoneDeviceId);
            }
            finally
            {
                AppDataPaths.ConfigureProductFolder(previous);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, TimeSpan.FromSeconds(30), "Multiple microphone ordering did not finish.");
    }

    [Fact]
    public void ExistingListIsNotReseeded()
    {
        AvaloniaTestThread.Run(() =>
        {
            var previous = AppDataPaths.ProductFolderName;
            AppDataPaths.ConfigureProductFolder("ClypDat-MultiMicTest-" + Guid.NewGuid().ToString("N"));
            var root = AppDataPaths.Root;
            try
            {
                var settings = new AppSettings { MicrophoneDeviceIds = ["id-b"] };
                var vm = Create(settings);

                vm.MultiMicrophoneEnabled = true;

                Assert.Equal(["id-b"], settings.MicrophoneDeviceIds);
            }
            finally
            {
                AppDataPaths.ConfigureProductFolder(previous);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, TimeSpan.FromSeconds(30), "Multiple microphone reseed check did not finish.");
    }
}
