namespace ClypDat.App.Services;

// The replay frame-timing setting: CFR or VFR (the configured frame rate is a
// ceiling in VFR), and the supported frame-rate range. The native recorder
// does the pacing itself (RecordingFramePacer, capture_final_hold).
public static class ReplayFrameTimingPolicy
{
    public const int MinimumFrameRate = 30;
    public const int MaximumFrameRate = 120;
    public const string Variable = "VFR";
    public const string Constant = "CFR";

    public static string Normalize(string? value) =>
        string.Equals(value, Variable, StringComparison.OrdinalIgnoreCase) ? Variable : Constant;
}
