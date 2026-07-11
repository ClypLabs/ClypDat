namespace Eve.App.Services;

public sealed record ResolutionOption(string Label, int Height)
{
    public override string ToString() => Label;
}
