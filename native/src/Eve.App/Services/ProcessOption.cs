namespace Eve.App.Services;

public sealed record ProcessOption(string Name, string Path = "", string WindowTitle = "")
{
    public override string ToString() => string.IsNullOrWhiteSpace(WindowTitle)
        ? Name
        : $"[{Name}]: {WindowTitle}";
}
