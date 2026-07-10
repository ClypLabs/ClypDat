namespace Eve.App.Services;

public sealed record ReplayBackendPreset(string Label, string Value, string Description)
{
    public override string ToString() => Label;
}
