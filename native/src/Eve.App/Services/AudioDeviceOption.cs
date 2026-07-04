namespace Eve.App.Services;

public sealed record AudioDeviceOption(string Id, string Name, bool IsDisabled = false)
{
    public override string ToString() => Name;
}
