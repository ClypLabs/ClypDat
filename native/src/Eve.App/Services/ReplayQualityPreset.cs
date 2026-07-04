namespace Eve.App.Services;

public sealed record ReplayQualityPreset(string Label, int MaxHeight, int FrameRate)
{
    public override string ToString() => $"{Label} ({MaxHeight}p, {FrameRate} FPS)";
}
