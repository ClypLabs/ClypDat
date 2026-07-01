namespace Eve.Capture.Abstractions;

public sealed record ActiveAppInfo(
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    bool IsFullscreenCandidate);
