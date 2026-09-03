namespace ClypDat.App.Services;

public sealed record AudioDeviceOption(string Id, string Name, bool IsDisabled = false)
{
    // Sentinel id for "use whatever Windows currently considers the default
    // capture device" - resolved fresh at every capture start so it tracks
    // the live Windows default instead of freezing on whatever device was
    // default at the moment the user picked it.
    public const string DefaultDeviceId = "default";

    public override string ToString() => Name;
}

internal readonly record struct AudioDeviceSelectionChange(bool ShouldPersist, string DeviceId)
{
    public static AudioDeviceSelectionChange FromPicker(AudioDeviceOption? selection, bool isApplyingSnapshot) =>
        new(!isApplyingSnapshot, selection?.Id ?? string.Empty);
}
