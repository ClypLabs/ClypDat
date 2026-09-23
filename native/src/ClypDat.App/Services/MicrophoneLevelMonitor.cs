using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClypDat.App.Services;

// Independent native capture handle shares the recorder's WASAPI and microphone
// filter modules. Managed code polls a coalesced scalar; no PCM crosses the ABI.
[SupportedOSPlatform("windows")]
internal sealed class MicrophoneLevelMonitor : IDisposable
{
    public const double FloorDb = -100;
    private readonly object _lock = new();
    private IntPtr _meter;
    private Timer? _timer;
    public event EventHandler<double>? LevelChanged;
    public bool IsRunning { get { lock (_lock) return _meter != IntPtr.Zero; } }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Text { internal IntPtr Data; internal uint Length, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Config
    {
        internal uint Size, Version;
        internal Text Device, Ffmpeg, Model;
        internal uint Suppression, Reserved;
        internal double Gate;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDelegate(ref Config config, out IntPtr meter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LevelDelegate(IntPtr meter, out float level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyDelegate(IntPtr meter);
    private static class Api
    {
        internal static readonly CreateDelegate Create = Load<CreateDelegate>("cd_audio_meter_create");
        internal static readonly LevelDelegate Level = Load<LevelDelegate>("cd_audio_meter_level");
        internal static readonly DestroyDelegate Destroy = Load<DestroyDelegate>("cd_audio_meter_destroy");
        private static T Load<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(NativeRecorderLibrary.Handle, name));
    }

    public unsafe void Start(string deviceId, bool noiseSuppression, double gateThresholdDb)
    {
        Stop();
        var device = string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDeviceOption.DefaultDeviceId ? string.Empty : deviceId;
        var ffmpeg = FfmpegPathResolver.FfmpegPath;
        var model = FfmpegPathResolver.RnnoiseModelPath;
        lock (_lock)
        {
            fixed (char* devicePointer = device, ffmpegPointer = ffmpeg, modelPointer = model)
            {
                var config = new Config
                {
                    Size = (uint)Marshal.SizeOf<Config>(), Version = 3,
                    Device = new() { Data = (IntPtr)devicePointer, Length = (uint)device.Length },
                    Ffmpeg = new() { Data = (IntPtr)ffmpegPointer, Length = (uint)ffmpeg.Length },
                    Model = new() { Data = (IntPtr)modelPointer, Length = (uint)model.Length },
                    Suppression = noiseSuppression ? 1u : 0u,
                    Gate = double.IsFinite(gateThresholdDb) ? Math.Clamp(gateThresholdDb, -100, -25) : -100
                };
                var result = Api.Create(ref config, out _meter);
                if (result != 0) throw new InvalidOperationException($"Native microphone test could not start ({result}). Check the selected device and reinstall ClypDat if native components are missing.");
            }
            var meter = _meter;
            _timer = new Timer(_ => Poll(meter), null, 0, 50);
            AppLog.Info($"Native mic test started: device={deviceId}, denoise={noiseSuppression}, gate={gateThresholdDb:0.#}dB.");
        }
    }

    private void Poll(IntPtr meter)
    {
        lock (_lock)
        {
            if (_meter != meter || meter == IntPtr.Zero) return;
            if (Api.Level(meter, out var level) == 0) LevelChanged?.Invoke(this, level);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose(); _timer = null;
            var meter = _meter; _meter = IntPtr.Zero;
            if (meter == IntPtr.Zero) return;
            // Failed native joins retain their own live graph until process exit.
            var result = Api.Destroy(meter);
            if (result != 0) AppLog.Info($"Native microphone teardown requires process restart ({result}).");
            LevelChanged?.Invoke(this, FloorDb);
        }
    }
    public void Dispose() => Stop();
}
