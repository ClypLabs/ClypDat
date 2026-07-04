namespace Eve.App.Services;

public sealed record ProcessOption(string Name)
{
    public override string ToString() => Name;
}
