using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class HdrCompatibilityPresentation
{
    internal static string Resolve(ReplayHdrCompatibilityStatus status, bool enabled) =>
        !enabled ? "Off" : status switch
        {
            ReplayHdrCompatibilityStatus.SdrDisplay => "SDR display",
            ReplayHdrCompatibilityStatus.PreparingConversion => "Preparing SDR conversion",
            ReplayHdrCompatibilityStatus.ConversionActive => "HDR conversion active",
            ReplayHdrCompatibilityStatus.ConversionFailed => "HDR conversion failed",
            _ => "HDR status unknown"
        };
}
