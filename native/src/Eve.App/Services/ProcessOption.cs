namespace Eve.App.Services;

public sealed record ProcessOption(string Name, string Path = "")
{
    public override string ToString() => Name;
}
