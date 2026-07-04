namespace Eve.App.Services;

public sealed record ExportCodecOption(string Label, string NvencEncoder, string SoftwareEncoder)
{
    public override string ToString() => Label;
}
