using System.Runtime.InteropServices;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Boundary for ClypDat.Capture.Native.dll. This deliberately contains no D3D11,
// FFmpeg, callback, or ownership-bearing pointer. Managed code owns only the
// SafeHandle; every native frame and packet stays inside the native engine.
internal static class NativeReplayEngineAbi
{
    internal const uint Version = 1;
    internal const uint EngineVersion = 1;
    internal const string LibraryName = "ClypDat.Capture.Native";

    internal enum Result : int
    {
        Ok = 0,
        InvalidArgument = -1,
        UnsupportedAbi = -2,
        InvalidState = -3,
        DeviceFailure = -4,
        Unavailable = -5,
        BufferTooSmall = -6
    }

    internal enum EngineState : uint { Created, Running, Paused, Stopped, Failed }
    internal enum CaptureRoute : uint { None, Dxgi, Wgc }
    internal enum FatalError : uint { None, Device, Encoder, Capture, Abi }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        internal uint StructSize;
        internal uint AbiVersion;

        internal static Header Create<T>() where T : struct => new()
        {
            StructSize = checked((uint)Marshal.SizeOf<T>()),
            AbiVersion = Version
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineConfig
    {
        internal Header Header;
        internal ulong TargetWindow;
        internal uint SelectedFps;
        internal uint Width;
        internal uint Height;
        internal uint Codec;
        internal uint EncoderMode;
        internal uint HistorySeconds;
        internal uint Flags;

        internal static EngineConfig From(ReplayBufferConfig config) => new()
        {
            Header = NativeReplayEngineAbi.Header.Create<EngineConfig>(),
            TargetWindow = unchecked((ulong)config.GameWindowHandle),
            SelectedFps = checked((uint)Math.Clamp(config.FrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate)),
            Width = checked((uint)MakeEven(config.CaptureWidth > 0 ? config.CaptureWidth : Math.Max(2, config.MaxHeight * 16 / 9))),
            Height = checked((uint)MakeEven(config.CaptureHeight > 0 ? config.CaptureHeight : Math.Max(2, config.MaxHeight))),
            Codec = string.Equals(ReplayVideoCodecPolicy.Normalize(config.VideoCodec), ReplayVideoCodecPolicy.Av1, StringComparison.OrdinalIgnoreCase) ? 2u : 1u,
            EncoderMode = string.Equals(config.EncoderMode, ReplayVideoCodecPolicy.Cpu, StringComparison.OrdinalIgnoreCase) ? 2u : 1u,
            HistorySeconds = checked((uint)Math.Clamp(config.DurationSeconds, 30, 1200)),
            Flags = config.CaptureCursor ? 1u : 0u
        };

        private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineHealth
    {
        internal Header Header;
        internal uint EngineVersion;
        internal EngineState State;
        internal uint SelectedFps;
        internal uint ActiveFps;
        internal CaptureRoute CaptureRoute;
        internal FatalError FatalError;
        internal uint QueueDepth;
        internal uint QueueCapacity;
        internal uint SurfacesInUse;
        internal uint SurfaceCapacity;
        internal uint AdapterLuidLow;
        internal int AdapterLuidHigh;
        internal double EncoderSlotWaitP95Ms;
        internal double SubmissionP95Ms;
        internal double QueueAgeMs;
        internal double InputFps;
        internal double OutputFps;
        internal double FreshFps;

        internal static EngineHealth CreateRequest() => new() { Header = NativeReplayEngineAbi.Header.Create<EngineHealth>() };
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_create(in EngineConfig config, out NativeReplayEngineHandle engine);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_start(NativeReplayEngineHandle engine);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_stop(IntPtr engine);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void cd_engine_destroy(IntPtr engine);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_set_paused(NativeReplayEngineHandle engine, uint paused);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_set_active_fps(NativeReplayEngineHandle engine, uint activeFps);
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern Result cd_engine_get_health(NativeReplayEngineHandle engine, ref EngineHealth health);
}

internal sealed class NativeReplayEngineHandle : SafeHandle
{
    private NativeReplayEngineHandle() : base(IntPtr.Zero, ownsHandle: true) { }
    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override bool ReleaseHandle()
    {
        NativeReplayEngineAbi.cd_engine_destroy(handle);
        return true;
    }
}

// Not wired into capture selection until native capture and packet remux are
// complete. This keeps probe failures side-effect-free and current sessions on
// the proven managed engine while ABI work lands in independently testable steps.
internal sealed class NativeReplayEngine : IDisposable
{
    private readonly NativeReplayEngineHandle _handle;

    private NativeReplayEngine(NativeReplayEngineHandle handle) => _handle = handle;

    internal static bool TryCreate(ReplayBufferConfig config, out NativeReplayEngine? engine, out string error)
    {
        engine = null;
        error = string.Empty;
        try
        {
            var result = NativeReplayEngineAbi.cd_engine_create(NativeReplayEngineAbi.EngineConfig.From(config), out var handle);
            if (result != NativeReplayEngineAbi.Result.Ok)
            {
                handle?.Dispose();
                error = $"native create returned {result}";
                return false;
            }
            engine = new NativeReplayEngine(handle);
            return true;
        }
        catch (DllNotFoundException) { error = "native DLL unavailable"; return false; }
        catch (EntryPointNotFoundException) { error = "native ABI unavailable"; return false; }
        catch (BadImageFormatException) { error = "native DLL architecture mismatch"; return false; }
    }

    internal bool TryStart(out string error) => TryInvoke(() => NativeReplayEngineAbi.cd_engine_start(_handle), out error);
    internal bool TrySetPaused(bool paused, out string error) => TryInvoke(() => NativeReplayEngineAbi.cd_engine_set_paused(_handle, paused ? 1u : 0u), out error);
    internal bool TrySetActiveFps(int fps, out string error) => TryInvoke(() => NativeReplayEngineAbi.cd_engine_set_active_fps(_handle, checked((uint)fps)), out error);

    internal bool TryGetHealth(out ReplayCaptureHealth health, out string error)
    {
        var native = NativeReplayEngineAbi.EngineHealth.CreateRequest();
        if (!TryInvoke(() => NativeReplayEngineAbi.cd_engine_get_health(_handle, ref native), out error))
        {
            health = ReplayCaptureHealth.Unknown("Native C++");
            return false;
        }
        health = MapHealth(native);
        return true;
    }

    internal static ReplayCaptureHealth MapHealth(NativeReplayEngineAbi.EngineHealth native)
    {
        var state = native.State switch
        {
            NativeReplayEngineAbi.EngineState.Created => ReplayCaptureState.Starting,
            NativeReplayEngineAbi.EngineState.Running or NativeReplayEngineAbi.EngineState.Paused => ReplayCaptureState.Healthy,
            NativeReplayEngineAbi.EngineState.Stopped => ReplayCaptureState.Stopped,
            _ => ReplayCaptureState.Failed
        };
        var route = native.CaptureRoute switch
        {
            NativeReplayEngineAbi.CaptureRoute.Dxgi => "DXGI",
            NativeReplayEngineAbi.CaptureRoute.Wgc => "WGC",
            _ => "Unbound"
        };
        var failure = native.FatalError == NativeReplayEngineAbi.FatalError.None ? string.Empty : $"Native {native.FatalError}";
        return new ReplayCaptureHealth("Native C++", route, state,
            checked((int)native.ActiveFps), native.InputFps, native.FreshFps, native.OutputFps,
            0, 0, checked((int)native.QueueDepth), string.Empty, string.Empty, failure, DateTime.UtcNow)
        {
            ConfiguredFrameRate = checked((int)native.SelectedFps),
            EncoderInputPath = "GPU resident",
            EncodeQueueCapacity = checked((int)native.QueueCapacity),
            AdapterDescription = $"LUID {native.AdapterLuidHigh:X8}:{native.AdapterLuidLow:X8}",
            FrameRateMode = "CFR",
            DegradeReason = native.FatalError == NativeReplayEngineAbi.FatalError.Encoder ? ReplayDegradeReason.EncoderOverload : ReplayDegradeReason.None
        };
    }

    private static bool TryInvoke(Func<NativeReplayEngineAbi.Result> action, out string error)
    {
        try
        {
            var result = action();
            error = result == NativeReplayEngineAbi.Result.Ok ? string.Empty : $"native call returned {result}";
            return result == NativeReplayEngineAbi.Result.Ok;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Dispose() => _handle.Dispose();
}
