namespace ClypDat.Capture.Abstractions;

public enum LinuxCaptureTargetKind { KdeWindow, KdeOutput }

// A compositor identity, never a Win32 HWND or an XWayland XID.
public sealed record LinuxCaptureTarget(LinuxCaptureTargetKind Kind, string Identifier)
{
    public string ToRecorderSource()
    {
        if (string.IsNullOrWhiteSpace(Identifier) || Identifier.Any(char.IsControl))
            throw new ArgumentException("A compositor target identifier is required.");
        return Kind switch
        {
            LinuxCaptureTargetKind.KdeWindow when Guid.TryParse(Identifier, out _) => "kde-window:" + Identifier,
            LinuxCaptureTargetKind.KdeOutput => "kde-output:" + Identifier,
            _ => throw new ArgumentException("Invalid KDE capture target.")
        };
    }
}

[Flags]
public enum ReplayBackendCapabilities
{
    None = 0,
    IsolatedWindow = 1,
    Monitor = 2,
    RamReplay = 4,
    FullSession = 8,
    SeparateApplicationAudio = 16,
    FocusFreeze = 32,
    ExactClipWindows = 64
}

public sealed record ReplayBackendReadiness(bool Ready, ReplayBackendCapabilities Capabilities, string Reason);

public interface IReplayBackendReadiness
{
    ReplayBackendReadiness GetReadiness();
}
