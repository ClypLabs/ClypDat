using ClypDat.Capture.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Output6 is Windows' authoritative active-output colour-space report. Keep
// this small and independent from capture so a display move/HDR toggle can be
// sampled with the recorder's existing one-second target check.
internal static class HdrCaptureCompatibility
{
    internal readonly record struct DisplayProfile(bool IsHdr, float SdrWhiteLevelNits, float PeakLuminanceNits)
    {
        public static DisplayProfile Unknown => new(false, 80, 1000);
    }

    public static DisplayProfile GetDisplayProfile(ID3D11Device device, nint monitor)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            for (uint index = 0; ; index++)
            {
                var result = adapter.EnumOutputs(index, out var output);
                if (result.Failure) break;
                using (output)
                {
                    if (output.Description.Monitor != monitor) continue;
                    using var output6 = output.QueryInterface<IDXGIOutput6>();
                    var description = output6.Description1;
                    // DXGI Output6 exposes active colour space and peak, but not
                    // Windows' per-display SDR white slider. Keep conversion
                    // deterministic until DisplayConfig metadata is available.
                    const float white = 80f;
                    var peak = description.MaxLuminance <= 0 ? 1000f : description.MaxLuminance;
                    AppLog.Info("Native capture: HDR SDR white level unavailable; assuming 80 nits.");
                    if (description.MaxLuminance <= 0) AppLog.Info("Native capture: HDR peak luminance unavailable; assuming 1000 nits.");
                    return new DisplayProfile(IsHdrColorSpace(description.ColorSpace), white, peak);
                }
            }
        }
        catch { }
        return DisplayProfile.Unknown;
    }

    public static ReplayHdrCompatibilityStatus Detect(ID3D11Device device, nint monitor)
    {
        var profile = GetDisplayProfile(device, monitor);
        return profile.IsHdr ? ReplayHdrCompatibilityStatus.PreparingConversion : ReplayHdrCompatibilityStatus.SdrDisplay;
    }

    // Windows desktop HDR commonly exposes linear scRGB (G10) rather than
    // PQ/HDR10. Both carry HDR headroom and require SDR conversion before the
    // existing BT.709 encoder path.
    internal static bool IsHdrColorSpace(ColorSpaceType colorSpace) => (int)colorSpace switch
    {
        1 => true, // RGB_FULL_G10_NONE_P709 (scRGB)
        12 or 13 or 14 or 16 => true, // ST.2084/PQ
        18 or 19 => true, // HLG
        25 => true, // RGB_FULL_G10_NONE_P2020
        _ => false
    };

}
