namespace Eve.App.Services;

public sealed record ReplayDurationPreset(string Label, int Seconds)
{
    public override string ToString() => Label;
}
