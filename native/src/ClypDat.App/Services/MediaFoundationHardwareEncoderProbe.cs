using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ClypDat.App.Services;

// Media Foundation encoders are COM MFTs selected at runtime by the graphics
// driver. Keep that discovery separate from capture so unavailable MFTs are a
// normal, diagnosable fallback condition rather than a start-up failure.
internal static class MediaFoundationHardwareEncoderProbe
{
    private const uint HardwareAndSorted = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);

    public static bool TryProbe(ID3D11Device device, out string detail)
    {
        IMFDXGIDeviceManager? manager = null;
        nint activations = 0;
        uint activationCount = 0;
        var started = false;

        try
        {
            MediaFactory.MFStartup();
            started = true;
            manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device).CheckError();

            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                HardwareAndSorted,
                null,
                null,
                out activations,
                out activationCount);

            if (activationCount == 0)
            {
                detail = "no hardware video encoder MFT";
                return false;
            }

            detail = $"{activationCount} hardware video encoder MFT(s) available";
            return true;
        }
        catch (Exception error)
        {
            detail = error.Message;
            return false;
        }
        finally
        {
            if (activations != 0)
            {
                for (var index = 0; index < activationCount; index++)
                {
                    var activation = Marshal.ReadIntPtr(activations, index * IntPtr.Size);
                    if (activation != 0) Marshal.Release(activation);
                }

                Marshal.FreeCoTaskMem(activations);
            }
            manager?.Dispose();
            if (started) MediaFactory.MFShutdown();
        }
    }
}
