using NAudio.CoreAudioApi;

namespace Eve.App.Services;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceOption> GetRenderDevices(bool includeDisabled)
    {
        var devices = Enumerate(DataFlow.Render);
        return includeDisabled
            ? new[] { new AudioDeviceOption(string.Empty, "Disabled", true) }.Concat(devices).ToArray()
            : devices;
    }

    public IReadOnlyList<AudioDeviceOption> GetCaptureDevices()
    {
        return Enumerate(DataFlow.Capture);
    }

    private static IReadOnlyList<AudioDeviceOption> Enumerate(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(device => new AudioDeviceOption(device.ID, device.FriendlyName))
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<AudioDeviceOption>();
        }
    }
}
