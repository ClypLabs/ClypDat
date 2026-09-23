using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClypDat.Capture.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace ClypDat.App.Services;

internal sealed unsafe class NativeRecorderSession : SafeHandleZeroOrMinusOneIsInvalid
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Header { public uint Size, Version; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Text16 { public IntPtr Data; public uint Length, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Texts16 { public IntPtr Data; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Application { public Text16 Name; public int Gain; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Bytes { public byte* Data; public uint Capacity, Required; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Rectangle { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct NormalizedRectangle { public double X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct DetectorCopy
    {
        public Header Header; public long TimestampUs; public fixed uint Widths[4]; public fixed uint Heights[4];
        public Bytes First, Second, Third, Mask;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct EventPoll
    {
        public Header Header; public ulong AfterSequence; public uint TimeoutMs, Mask; public ulong Sequence;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Configuration
    {
        public Header Header;
        public long QpcAnchor, QpcFrequency, UtcAnchorTicks;
        public Guid BootId;
        public ulong Window, Monitor;
        public int DurationSeconds, MaxHeight, FrameRate, CaptureX, CaptureY, CaptureWidth, CaptureHeight;
        public int BitrateMbps, GameGainPercent, MicrophoneGainPercent, FullSessionQuotaGb;
        public uint Flags;
        public double MicrophoneGateDb;
        public Text16 ChatDeviceName, ChatDeviceId, MicrophoneDeviceName;
        public Texts16 ChatProcesses, MicrophoneDevices, ExcludedProcesses;
        public IntPtr Applications;
        public uint ApplicationCount, Reserved;
        public Text16 GameName, GameExecutable, GameWindowTitle, GameWindowClass;
        public Text16 VideoCodec, EncoderMode, EncoderProfile, FrameRateMode, PacingMode;
        public Text16 CaptureSource, MonitorDeviceName, ProcessPriority, MicrophoneChannelMode;
        public Text16 WorkDirectory, FfmpegPath, RnnoiseModel, FullSessionPath;
        public Text16 FullSessionCodec, FullSessionContainer, LibraryFolder;
        public Text16 FileNameScheme, CustomFileNameTemplate, SaveHotkey, FullSessionHotkey;
        public Text16 DiagnosticForceDxgi, DiagnosticDisableDirectBlt, DiagnosticPacingPolicy, DiagnosticNvencDelay, DiagnosticD3dDebug;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Contract
    {
        public Header Header;
        public uint PointerSize, ConfigSize, SaveRequestSize, SaveStatusSize, HealthSize, OverlaySize, ArtworkSize, EventSize;
        public ulong Capabilities;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Health
    {
        public Header Header;
        public uint Running, Paused, RestartRequired, ActiveFps, QueueDepth, QueueCapacity;
        public ulong Acquired, Encoded, Replaced, Generation, DetectorCopies, InputRevision;
        public Bytes Details;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct SaveRequest { public Header Header; public Guid Id; public long StartUs, EndUs; public Text16 Output; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct SaveStatus
    {
        public Header Header; public uint State, Frozen; public long DurationUs; public ulong Generation; public Bytes Details;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Overlay
    {
        public Header Header; public ulong Revision; public long AtUs; public uint Burned, Reserved;
        public double CameraX, CameraY, CameraWidth, KeyboardX, KeyboardY, KeyboardWidth;
        public Text16 CameraMoniker, CameraName, KeyboardLayout, SettingsJson;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Artwork
    {
        public Header Header; public ulong Revision; public long AtUs; public uint Width, Height, Stride, Premultiplied;
        public byte* Pixels; public uint Length, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct Frame
    {
        public Header Header; public ulong Revision; public long TimestampUs; public uint Width, Height, Stride, Reserved; public Bytes Pixels;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ContractCall(Contract* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCall(Configuration* value, out IntPtr recorder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleCall(IntPtr recorder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DestroyCall(IntPtr recorder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NumberCall(IntPtr recorder, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HealthCall(IntPtr recorder, Health* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SaveCall(IntPtr recorder, SaveRequest* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StatusCall(IntPtr recorder, Guid* id, SaveStatus* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IdCall(IntPtr recorder, Guid* id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OverlayCall(IntPtr recorder, Overlay* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ArtworkCall(IntPtr recorder, Artwork* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int KeysCall(IntPtr recorder, Bytes* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FrameCall(IntPtr recorder, Frame* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegionsCall(IntPtr recorder, Rectangle* regions, uint count, Rectangle* masks, uint maskCount, uint width, uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DetectorSetCall(IntPtr recorder, NormalizedRectangle* regions, uint count, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DetectorReadCall(IntPtr recorder, DetectorCopy* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EventsCall(IntPtr recorder, EventPoll* value);
    private sealed class Functions
    {
        public readonly ContractCall Contract = Export<ContractCall>("get_contract");
        public readonly CreateCall Create = Export<CreateCall>("create");
        public readonly SimpleCall Start = Export<SimpleCall>("start"), Stop = Export<SimpleCall>("stop");
        public readonly DestroyCall Destroy = Export<DestroyCall>("destroy");
        public readonly NumberCall Pause = Export<NumberCall>("pause"), FrameRate = Export<NumberCall>("frame_rate");
        public readonly HealthCall Health = Export<HealthCall>("health");
        public readonly SaveCall Save = Export<SaveCall>("save_begin");
        public readonly StatusCall Status = Export<StatusCall>("save_status");
        public readonly IdCall Cancel = Export<IdCall>("save_cancel"), Release = Export<IdCall>("save_release");
        public readonly OverlayCall Overlay = Export<OverlayCall>("overlay");
        public readonly ArtworkCall Artwork = Export<ArtworkCall>("artwork");
        public readonly KeysCall Keys = Export<KeysCall>("keys");
        public readonly FrameCall Detector = Export<FrameCall>("detector_copy"), Camera = Export<FrameCall>("camera_copy");
        public readonly RegionsCall Regions = Export<RegionsCall>("detector_regions");
        public readonly DetectorSetCall DetectorSet = Export<DetectorSetCall>("detector_set");
        public readonly DetectorReadCall DetectorRead = Export<DetectorReadCall>("detector_read");
        public readonly EventsCall Events = Export<EventsCall>("events");
        private static T Export<T>(string suffix) where T : Delegate
        {
            try { return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(NativeRecorderLibrary.Handle, "cd_recorder_" + suffix)); }
            catch (EntryPointNotFoundException error) { throw new BadImageFormatException("Native recording ABI v3 is missing. Reinstall ClypDat.", error); }
        }
    }
    private static readonly Lazy<Functions> Api = new(() => new());
    private NativeRecorderSession() : base(true) { }
    private string? _workDirectory;
    private bool _stopped;
    private int _acceptedSaves;
    private static Header Version<T>() where T : unmanaged => new() { Size = (uint)sizeof(T), Version = 3 };
    internal static Contract ReadContract()
    {
        var value = new Contract { Header = Version<Contract>() };
        Check(Api.Value.Contract(&value), "negotiate native recorder ABI");
        if (value.PointerSize != IntPtr.Size || value.ConfigSize != sizeof(Configuration) || value.SaveRequestSize != sizeof(SaveRequest) ||
            value.SaveStatusSize != sizeof(SaveStatus) || value.HealthSize != sizeof(Health) || value.OverlaySize != sizeof(Overlay) || value.ArtworkSize != sizeof(Artwork) || value.EventSize != sizeof(EventPoll))
            throw new BadImageFormatException("Native recording ABI structure sizes do not match. Reinstall ClypDat.");
        return value;
    }
    internal static void RequireCompleteEngine()
    {
        RequireCapabilities(ReadContract().Capabilities);
    }
    internal static void RequireCapabilities(ulong capabilities)
    {
        const ulong required = 1 | 2 | 4 | 8 | 16 | 32;
        if ((capabilities & required) != required)
            throw new NotSupportedException("Native recorder is incomplete: capture, audio, saves, full sessions, overlays, and asynchronous control are required. Install a complete ClypDat recorder build.");
    }
    internal static NativeRecorderSession Create(ReplayBufferConfig settings, string workDirectory, string fullSessionPath)
    {
        RequireCompleteEngine();
        using var memory = new Inputs();
        var configuration = Map(settings, workDirectory, fullSessionPath, memory);
        Check(Api.Value.Create(&configuration, out var handle), "create native recorder");
        var recorder = new NativeRecorderSession { _workDirectory = Path.GetFullPath(workDirectory) }; recorder.SetHandle(handle); return recorder;
    }
    private static Configuration Map(ReplayBufferConfig s, string root, string fullSessionPath, Inputs memory)
    {
        var applications = (s.AdditionalAudioProcesses ?? new Dictionary<string, int>()).Select(pair => new Application { Name = memory.Text(pair.Key), Gain = pair.Value }).ToArray();
        return new()
        {
            Header = Version<Configuration>(), QpcAnchor = MonotonicClock.QpcAnchor, QpcFrequency = Stopwatch.Frequency,
            UtcAnchorTicks = MonotonicClock.UtcAnchor.Ticks, BootId = Guid.Parse(MonotonicClock.BootId), Window = unchecked((ulong)s.GameWindowHandle),
            DurationSeconds = s.DurationSeconds, MaxHeight = s.MaxHeight, FrameRate = s.FrameRate, CaptureX = s.CaptureX, CaptureY = s.CaptureY,
            CaptureWidth = s.CaptureWidth, CaptureHeight = s.CaptureHeight, BitrateMbps = s.BitrateMbps, GameGainPercent = s.GameAudioVolumePercent,
            MicrophoneGainPercent = s.MicrophoneVolumePercent, FullSessionQuotaGb = s.FullSessionQuotaGb,
            Flags = (s.CaptureCursor ? 1u : 0) | (s.FullSessionRecordingEnabled ? 2u : 0) | (s.FullSessionBackgroundFinalize ? 4u : 0) |
                (s.MicrophoneNoiseSuppressionEnabled ? 8u : 0) | (s.AdaptiveFrameRateProtectionEnabled ? 16u : 0) | (s.ReplayHdrCompatibilityEnabled ? 32u : 0),
            MicrophoneGateDb = s.MicrophoneNoiseGateThresholdDb, ChatDeviceName = memory.Text(s.ChatAudioDeviceName), ChatDeviceId = memory.Text(s.ChatAudioDeviceId),
            MicrophoneDeviceName = memory.Text(s.MicrophoneDeviceName), ChatProcesses = memory.Texts(s.ChatAudioProcessNames),
            MicrophoneDevices = memory.Texts(s.MicrophoneDeviceIds), ExcludedProcesses = memory.Texts(s.GameAudioExcludedProcesses),
            Applications = memory.Array(applications), ApplicationCount = (uint)applications.Length,
            GameName = memory.Text(s.GameDisplayName), GameExecutable = memory.Text(s.GameExecutableName), GameWindowTitle = memory.Text(s.GameWindowTitle),
            GameWindowClass = memory.Text(s.GameWindowClass), VideoCodec = memory.Text(s.VideoCodec), EncoderMode = memory.Text(s.EncoderMode),
            EncoderProfile = memory.Text(s.EncoderProfile), FrameRateMode = memory.Text(s.FrameRateMode), PacingMode = memory.Text(s.FramePacingMode),
            CaptureSource = memory.Text(s.CaptureSource), MonitorDeviceName = memory.Text(s.CaptureMonitorDeviceName), ProcessPriority = memory.Text(s.ProcessPriority),
            MicrophoneChannelMode = memory.Text(s.MicrophoneChannelMode), WorkDirectory = memory.Text(Path.GetFullPath(root)),
            FfmpegPath = memory.Text(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe")),
            RnnoiseModel = memory.Text(Path.Combine(AppContext.BaseDirectory, "rnnoise", "lq.rnnn")), FullSessionPath = memory.Text(fullSessionPath),
            FullSessionCodec = memory.Text(s.FullSessionVideoCodec), FullSessionContainer = memory.Text(s.FullSessionContainer), LibraryFolder = memory.Text(s.LibraryFolder),
            FileNameScheme = memory.Text(s.ClipFileNameScheme), CustomFileNameTemplate = memory.Text(s.CustomClipFileNameTemplate), SaveHotkey = memory.Text(s.SaveReplayHotkey),
            FullSessionHotkey = memory.Text(s.FullSessionHotkey), DiagnosticForceDxgi = memory.Environment("CLYPDAT_FORCE_DXGI"),
            DiagnosticDisableDirectBlt = memory.Environment("CLYPDAT_DISABLE_DIRECT_BLT"), DiagnosticPacingPolicy = memory.Environment("CLYPDAT_PACING_POLICY"),
            DiagnosticNvencDelay = memory.Environment("CLYPDAT_NVENC_DELAY"), DiagnosticD3dDebug = memory.Environment("CLYPDAT_D3D_DEBUG")
        };
    }
    internal void Start() { Command(Api.Value.Start(handle), "start native recorder"); GC.KeepAlive(this); }
    internal EventPoll WaitEvents(ulong afterSequence, uint timeoutMs = 100)
    {
        var value = new EventPoll { Header = Version<EventPoll>(), AfterSequence = afterSequence, TimeoutMs = Math.Min(100, timeoutMs) };
        Command(Api.Value.Events(handle, &value), "consume native recorder events"); GC.KeepAlive(this); return value;
    }
    internal void Stop() { Command(Api.Value.Stop(handle), "stop native recorder; worker restart required if teardown timed out"); _stopped = true; GC.KeepAlive(this); }
    internal void Pause(bool paused) { Command(Api.Value.Pause(handle, paused ? 1u : 0), "pause native recorder"); GC.KeepAlive(this); }
    internal void FrameRate(int value) { Command(Api.Value.FrameRate(handle, checked((uint)value)), "change native recorder frame rate"); GC.KeepAlive(this); }
    internal (Health Value, JsonElement Details) ReadHealth()
    {
        var value = new Health { Header = Version<Health>() };
        var result = Api.Value.Health(handle, &value);
        if (result != -6) Check(result, "read native recorder health");
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            var bytes = Buffer(value.Details.Required);
            fixed (byte* data = bytes) { value.Details.Data = data; value.Details.Capacity = (uint)bytes.Length; result = Api.Value.Health(handle, &value); }
            if (result == -6) continue;
            Check(result, "copy native recorder health"); GC.KeepAlive(this);
            value.Details.Data = null;
            return (value, Json(bytes, value.Details.Required));
        }
        GC.KeepAlive(this); throw new InvalidDataException("Native recorder health changed size repeatedly during copying.");
    }
    internal void BeginSave(Guid id, long startUs, long endUs, string output)
    {
        using var memory = new Inputs(); var value = new SaveRequest { Header = Version<SaveRequest>(), Id = id, StartUs = startUs, EndUs = endUs, Output = memory.Text(output) };
        Command(Api.Value.Save(handle, &value), "begin native replay save"); Interlocked.Increment(ref _acceptedSaves); GC.KeepAlive(this);
    }
    internal (SaveStatus Value, JsonElement Details) ReadSave(Guid id)
    {
        var value = new SaveStatus { Header = Version<SaveStatus>() };
        var result = Api.Value.Status(handle, &id, &value);
        if (result != -6) Check(result, "read native replay save");
        if (value.State == 0) { GC.KeepAlive(this); return (value, default); }
        var bytes = Buffer(value.Details.Required);
        fixed (byte* data = bytes) { value.Details.Data = data; value.Details.Capacity = (uint)bytes.Length; Check(Api.Value.Status(handle, &id, &value), "copy native replay save result"); }
        GC.KeepAlive(this); return (value, Json(bytes, value.Details.Required));
    }
    internal void CancelSave(Guid id) { Command(Api.Value.Cancel(handle, &id), "cancel native replay save"); GC.KeepAlive(this); }
    internal void ReleaseSave(Guid id) { Command(Api.Value.Release(handle, &id), "release native replay save"); Interlocked.Decrement(ref _acceptedSaves); GC.KeepAlive(this); }
    internal void UpdateOverlay(OverlayCaptureSettings s, ulong revision, long atUs)
    {
        using var memory = new Inputs();
        var value = new Overlay { Header = Version<Overlay>(), Revision = revision, AtUs = atUs, Burned = OverlayRecordingMode.IsBurned(s.RecordingMode) ? 1u : 0,
            CameraX = s.CameraTransform.X, CameraY = s.CameraTransform.Y, CameraWidth = s.CameraTransform.Width,
            KeyboardX = s.KeyboardTransform.X, KeyboardY = s.KeyboardTransform.Y, KeyboardWidth = s.KeyboardTransform.Width,
            CameraMoniker = memory.Text(s.Camera?.DeviceMoniker), CameraName = memory.Text(s.Camera?.FriendlyName), KeyboardLayout = memory.Text(s.KeyboardLayout),
            SettingsJson = memory.Text(JsonSerializer.Serialize(s)) };
        Command(Api.Value.Overlay(handle, &value), "update native overlays"); GC.KeepAlive(this);
    }
    internal void UpdateArtwork(RecordingKeyboardArtwork artwork)
    {
        fixed (byte* pixels = artwork.PremultipliedBgra)
        {
            var value = new Artwork { Header = Version<Artwork>(), Revision = artwork.Revision, AtUs = artwork.TimestampMicroseconds,
                Width = (uint)artwork.Width, Height = (uint)artwork.Height, Stride = (uint)artwork.Stride, Premultiplied = 1,
                Pixels = pixels, Length = (uint)artwork.PremultipliedBgra.Length };
            Command(Api.Value.Artwork(handle, &value), "update native keyboard artwork");
        }
        GC.KeepAlive(this);
    }
    internal IReadOnlyList<InputPhysicalKey> PressedKeys()
    {
        var value = new Bytes(); var result = Api.Value.Keys(handle, &value); if (result != -6) Check(result, "read native input");
        byte[]? bytes = null;
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            bytes = Buffer(value.Required);
            fixed (byte* data = bytes) { value.Data = data; value.Capacity = (uint)bytes.Length; result = Api.Value.Keys(handle, &value); }
            if (result != -6) break;
        }
        Check(result, "copy native input");
        GC.KeepAlive(this); var document = Json(bytes!, value.Required); var keys = new List<InputPhysicalKey>();
        string?[] buttons = [null, "MouseLeft", "MouseRight", "MouseMiddle", "MouseBack", "MouseForward"];
        foreach (var key in document.EnumerateArray()) keys.Add(new(key.GetProperty("ScanCode").GetUInt16(), key.GetProperty("E0").GetBoolean(),
            key.GetProperty("E1").GetBoolean(), buttons[Math.Clamp(key.GetProperty("MouseButton").GetInt32(), 0, 5)]));
        return keys;
    }
    internal void DetectorRegions(Rectangle[] regions, Rectangle[] masks, int width, int height)
    {
        fixed (Rectangle* r = regions, m = masks) Check(Api.Value.Regions(handle, r, (uint)regions.Length, m, (uint)masks.Length, (uint)width, (uint)height), "update native detector regions");
        GC.KeepAlive(this);
    }
    internal (Frame Value, byte[] Pixels)? DetectorFrame()
    {
        var value = new Frame { Header = Version<Frame>() }; var result = Api.Value.Detector(handle, &value);
        if (result == -5) { GC.KeepAlive(this); return null; }
        if (result != -6) Check(result, "read native detector frame"); var bytes = Buffer(value.Pixels.Required);
        fixed (byte* data = bytes) { value.Pixels.Data = data; value.Pixels.Capacity = (uint)bytes.Length; Check(Api.Value.Detector(handle, &value), "copy native detector frame"); }
        GC.KeepAlive(this); return (value, bytes);
    }
    internal void ConfigureDetector(DetectorRegionSet? regions)
    {
        NormalizedRectangle[] rectangles = regions is null ? [] : [Map(regions.First), Map(regions.Second), Map(regions.Third)];
        fixed (NormalizedRectangle* data = rectangles) Command(Api.Value.DetectorSet(handle, data, (uint)rectangles.Length,
            regions is not null && regions == global::ClypDat.App.Services.DetectorRegions.ForGame("helldivers2") ? 1u : 0), "set native detector regions");
        GC.KeepAlive(this);
        static NormalizedRectangle Map(NormalizedRegion region) => new() { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
    }
    internal DetectorFrameSnapshot? ReadDetector()
    {
        var value = new DetectorCopy { Header = Version<DetectorCopy>() };
        var result = Api.Value.DetectorRead(handle, &value);
        if (result == -5) { GC.KeepAlive(this); return null; }
        if (result != -6) Check(result, "read native detector regions");
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            var first = Buffer(value.First.Required); var second = Buffer(value.Second.Required);
            var third = Buffer(value.Third.Required); var mask = Buffer(value.Mask.Required);
            fixed (byte* a = first, b = second, c = third, d = mask)
            {
                value.First.Data = a; value.First.Capacity = (uint)first.Length;
                value.Second.Data = b; value.Second.Capacity = (uint)second.Length;
                value.Third.Data = c; value.Third.Capacity = (uint)third.Length;
                value.Mask.Data = d; value.Mask.Capacity = (uint)mask.Length;
                result = Api.Value.DetectorRead(handle, &value);
            }
            if (result == -6) continue;
            if (result == -5) { GC.KeepAlive(this); return null; }
            Check(result, "copy native detector regions"); GC.KeepAlive(this);
            return new(NativeRecordingAdapter.FromUs(value.TimestampUs),
                Image(first, value.Widths[0], value.Heights[0], value.First.Required), Image(second, value.Widths[1], value.Heights[1], value.Second.Required),
                Image(third, value.Widths[2], value.Heights[2], value.Third.Required), value.Mask.Required == 0 ? null : Image(mask, value.Widths[3], value.Heights[3], value.Mask.Required));
        }
        GC.KeepAlive(this); return null;
        static GrayDetectorImage Image(byte[] pixels, uint width, uint height, uint required)
        {
            if (width is 0 or > 16384 || height is 0 or > 16384) throw new InvalidDataException("Native detector image dimensions are invalid.");
            var count = checked((int)(width * height));
            if (count != required || count > pixels.Length) throw new InvalidDataException("Native detector image does not match its copied buffer.");
            return new((int)width, (int)height, pixels.AsSpan(0, count).ToArray());
        }
    }
    private static byte[] Buffer(uint required)
    {
        if (required > 256 * 1024 * 1024) throw new InvalidDataException("Native recorder result exceeds the copied-buffer limit.");
        return new byte[checked(required + 4096)];
    }
    private static JsonElement Json(byte[] bytes, uint count)
    {
        using var document = JsonDocument.Parse(bytes.AsMemory(0, checked((int)count))); return document.RootElement.Clone();
    }
    private static void Check(int result, string action) { if (result != 0) throw new InvalidOperationException($"Could not {action} (native status {result})."); }
    private void Command(int result, string action)
    {
        if (result == 0) return;
        string? reason = null;
        try
        {
            var details = ReadHealth().Details;
            if (details.TryGetProperty("controlError", out var error)) reason = error.GetString();
        }
        catch { }
        GC.KeepAlive(this);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? $"Could not {action} (native status {result})." : $"Could not {action}: {reason}");
    }
    protected override bool ReleaseHandle()
    {
        try
        {
            Api.Value.Destroy(handle);
            // Save operations hold DangerousAddRef through sidecar publication.
            // A successful native stop plus the final reference means no writer
            // or accepted save can still read this session-owned directory.
            if (_stopped && Volatile.Read(ref _acceptedSaves) == 0 && _workDirectory is { } directory)
            {
                var parent = Path.GetFullPath(Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "native-replay-buffer"));
                if (string.Equals(Path.GetDirectoryName(directory), parent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(directory).StartsWith("session-", StringComparison.Ordinal) && Directory.Exists(directory))
                {
                    try { Directory.Delete(directory, true); }
                    catch (Exception error) { AppLog.Debug($"Native recording session cleanup deferred: {directory}: {error.Message}"); }
                }
            }
            return true;
        }
        catch { return false; }
    }
    private sealed class Inputs : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        public Text16 Text(string? text)
        {
            if (string.IsNullOrEmpty(text)) return default;
            var pointer = Marshal.StringToHGlobalUni(text); _allocations.Add(pointer); return new() { Data = pointer, Length = (uint)text.Length };
        }
        public Text16 Environment(string name) => Text(System.Environment.GetEnvironmentVariable(name));
        public Texts16 Texts(IEnumerable<string> values) { var items = values.Select(Text).ToArray(); return new() { Data = Array(items), Count = (uint)items.Length }; }
        public IntPtr Array<T>(T[] values) where T : unmanaged
        {
            if (values.Length == 0) return IntPtr.Zero;
            var pointer = Marshal.AllocHGlobal(checked(values.Length * sizeof(T))); _allocations.Add(pointer);
            values.AsSpan().CopyTo(new Span<T>((void*)pointer, values.Length)); return pointer;
        }
        public void Dispose() { foreach (var value in _allocations) Marshal.FreeHGlobal(value); }
    }
}
