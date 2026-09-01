using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Deep adapter over the hook's keyed-mutex transport. A shared slot is copied
// into a processing-owned ring before its mutex is released, so crop, scale,
// encode, and leases never make the game wait.
internal unsafe sealed class GraphicsHookFrameSource : IGameFrameSource, IDisposable
{
    private const int Magic = 0x48444C43;
    private const int Version = 1;
    private const int Slots = 3;
    private const int NameChars = 128;
    private const int ControlBytes = 912;
    private const int LocatorBytes = 272;
    private const int StateOffset = 12;
    private const int FailureOffset = 16;
    private const int RequestedFpsOffset = 36;
    private const int FormatOffset = 44;
    private const int WidthOffset = 48;
    private const int HeightOffset = 52;
    private const int GenerationOffset = 60;
    private const int PresentsOffset = 68;
    private const int FramesOffset = 76;
    private const int DropsOffset = 84;
    private const int SequencesOffset = 92;
    private const int TimestampsOffset = 116;
    private const int NamesOffset = 140;
    private readonly ID3D11Device _device;
    private readonly object _gpuLock;
    private readonly MemoryMappedFile _controlMapping;
    private readonly MemoryMappedViewAccessor _controlView;
    private readonly MemoryMappedFile _locatorMapping;
    private readonly MemoryMappedViewAccessor _locatorView;
    private readonly byte* _control;
    private readonly ProcessingSlot[] _processingSlots = new ProcessingSlot[Slots];
    private ID3D11Texture2D[]? _sharedTextures;
    private IDXGIKeyedMutex[]? _sharedMutexes;
    private long _lastSequence;
    private long _generation;
    private string? _failure;
    private bool _disposed;

    private GraphicsHookFrameSource(ID3D11Device device, object gpuLock, uint processId, nint window, int frameRate)
    {
        _device = device;
        _gpuLock = gpuLock;
        var nonce = Guid.NewGuid().ToString("N");
        var controlName = $"Local\\ClypDat.GraphicsHook.Control.{processId}.{nonce}";
        _controlMapping = MemoryMappedFile.CreateNew(controlName, ControlBytes, MemoryMappedFileAccess.ReadWrite);
        _controlView = _controlMapping.CreateViewAccessor(0, ControlBytes, MemoryMappedFileAccess.ReadWrite);
        _controlView.SafeMemoryMappedViewHandle.AcquirePointer(ref _control);
        NativeMemory.Clear(_control, (nuint)ControlBytes);
        WriteInt32(0, Magic); WriteInt32(4, ControlBytes); WriteInt32(8, Version);
        WriteInt32(StateOffset, 1); WriteInt32(20, Environment.ProcessId); WriteInt32(24, unchecked((int)processId));
        WriteInt64(28, window); WriteInt32(RequestedFpsOffset, Math.Clamp(frameRate, 10, 480));
        for (var index = 0; index < Slots; index++)
            WriteString(NamesOffset + index * NameChars * sizeof(char), $"Local\\ClypDat.GraphicsHook.Frame.{processId}.{nonce}.{index}");

        var locatorName = $"Local\\ClypDat.GraphicsHook.Locator.{processId}";
        _locatorMapping = MemoryMappedFile.CreateNew(locatorName, LocatorBytes, MemoryMappedFileAccess.ReadWrite);
        _locatorView = _locatorMapping.CreateViewAccessor(0, LocatorBytes, MemoryMappedFileAccess.ReadWrite);
        _locatorView.Write(0, Magic); _locatorView.Write(4, LocatorBytes); _locatorView.Write(8, Version); _locatorView.Write(12, unchecked((int)processId));
        var locatorNameBytes = Encoding.Unicode.GetBytes(controlName + '\0');
        _locatorView.WriteArray(16, locatorNameBytes, 0, locatorNameBytes.Length);
    }

    public static bool TryCreate(ID3D11Device device, object gpuLock, ReplayBufferConfig config, out GraphicsHookFrameSource? source, out string failure)
    {
        source = null;
        failure = string.Empty;
        if (config.GameWindowHandle == 0) { failure = "Graphics hook needs a game window."; return false; }
        GetWindowThreadProcessId((nint)config.GameWindowHandle, out var processId);
        if (processId == 0) { failure = "Target game window no longer has a process."; return false; }
        try
        {
            var created = new GraphicsHookFrameSource(device, gpuLock, processId, (nint)config.GameWindowHandle, config.FrameRate);
            if (!created.Inject(processId, out failure) || !created.WaitForReady(TimeSpan.FromSeconds(5), out failure)) { created.Dispose(); return false; }
            source = created;
            return true;
        }
        catch (Exception error)
        {
            failure = error.Message;
            source?.Dispose();
            return false;
        }
    }

    public string CaptureMode => "Graphics Hook (D3D11)";
    public string? Failure => _failure;
    public (int Width, int Height) ContentSize => (ReadInt32(WidthOffset), ReadInt32(HeightOffset));
    internal GraphicsHookTelemetry Telemetry => new(ReadInt64(PresentsOffset), ReadInt64(FramesOffset), ReadInt64(DropsOffset), _failure);

    public void SetTargetFrameRate(int frameRate) => WriteInt32(RequestedFpsOffset, Math.Clamp(frameRate, 10, 480));

    public bool WaitAndTakeLatestFrame(TimeSpan timeout, CancellationToken cancellationToken, out GameFrameLease? frame)
    {
        frame = null;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
        {
            if (ReadInt32(StateOffset) is 4 or 6) { _failure ??= DescribeFailure(ReadInt32(FailureOffset)); return false; }
            if (!EnsureResources()) return false;
            for (var index = 0; index < Slots; index++)
            {
                var sequence = ReadInt64(SequencesOffset + index * sizeof(long));
                if (sequence <= _lastSequence) continue;
                var destination = _processingSlots.FirstOrDefault(slot => !slot.Leased);
                if (destination is null) return false;
                try
                {
                    _sharedMutexes![index].AcquireSync(1, 0);
                }
                catch
                {
                    continue;
                }
                try
                {
                    lock (_gpuLock) _device.ImmediateContext.CopyResource(destination.Texture, _sharedTextures![index]);
                }
                finally
                {
                    _sharedMutexes[index].ReleaseSync(0);
                }
                destination.Leased = true;
                _lastSequence = sequence;
                frame = new HookLease(destination, ReadInt64(TimestampsOffset + index * sizeof(long)), sequence, _generation);
                return true;
            }
            Thread.Sleep(2);
        }
        return false;
    }

    private bool Inject(uint processId, out string failure)
    {
        failure = string.Empty;
        var injector = Path.Combine(AppContext.BaseDirectory, "ClypDat.Hook.Injector.exe");
        var hook = CopyHookToCache();
        if (!File.Exists(injector) || hook is null) { failure = "Graphics hook payload is missing from this ClypDat build."; return false; }
        using var process = Process.Start(new ProcessStartInfo(injector, $"--pid {processId} --dll \"{hook}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
        if (process is null) { failure = "Could not start graphics hook injector."; return false; }
        if (!process.WaitForExit(5000)) { failure = "Graphics hook injection timed out."; return false; }
        failure = process.StandardError.ReadToEnd().Trim();
        return process.ExitCode == 0 || string.IsNullOrWhiteSpace(failure);
    }

    private string? CopyHookToCache()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "ClypDat.GraphicsHook.dll");
        if (!File.Exists(installed)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed))).ToLowerInvariant();
        var folder = Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "graphics-hook");
        Directory.CreateDirectory(folder);
        var cached = Path.Combine(folder, $"ClypDat.GraphicsHook.{hash}.dll");
        if (!File.Exists(cached)) File.Copy(installed, cached);
        return cached;
    }

    private bool WaitForReady(TimeSpan timeout, out string failure)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (ReadInt32(StateOffset) == 3 && ReadInt32(WidthOffset) > 0 && ReadInt32(HeightOffset) > 0) { failure = string.Empty; return true; }
            if (ReadInt32(StateOffset) == 4) { failure = DescribeFailure(ReadInt32(FailureOffset)); return false; }
            Thread.Sleep(20);
        }
        failure = "Graphics hook did not present a supported D3D11 frame within 5 seconds.";
        return false;
    }

    private bool EnsureResources()
    {
        var generation = ReadInt64(GenerationOffset);
        if (_sharedTextures is not null && generation == _generation) return true;
        DisposeResources();
        try
        {
            using var device1 = _device.QueryInterface<ID3D11Device1>();
            _sharedTextures = new ID3D11Texture2D[Slots];
            _sharedMutexes = new IDXGIKeyedMutex[Slots];
            for (var index = 0; index < Slots; index++)
            {
                var texture = device1.OpenSharedResourceByName<ID3D11Texture2D>(ReadString(NamesOffset + index * NameChars * sizeof(char)), unchecked((Vortice.Direct3D11.SharedResourceFlags)0x80000001u));
                _sharedTextures[index] = texture;
                _sharedMutexes[index] = texture.QueryInterface<IDXGIKeyedMutex>();
            }
            var width = ReadInt32(WidthOffset); var height = ReadInt32(HeightOffset);
            var format = (Format)ReadInt32(FormatOffset);
            for (var index = 0; index < Slots; index++)
                _processingSlots[index] = new ProcessingSlot(CreateOwnedTexture(width, height, format));
            _generation = generation;
            return true;
        }
        catch (Exception error)
        {
            _failure = $"Graphics hook transport failed: {error.Message}";
            return false;
        }
    }

    private ID3D11Texture2D CreateOwnedTexture(int width, int height, Format format) => _device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.None
    });

    private void DisposeResources()
    {
        if (_sharedMutexes is not null) foreach (var mutex in _sharedMutexes) mutex?.Dispose();
        if (_sharedTextures is not null) foreach (var texture in _sharedTextures) texture?.Dispose();
        foreach (var slot in _processingSlots) slot?.Texture.Dispose();
        _sharedMutexes = null; _sharedTextures = null;
    }

    private int ReadInt32(int offset) => Marshal.ReadInt32((nint)(_control + offset));
    private long ReadInt64(int offset) => Marshal.ReadInt64((nint)(_control + offset));
    private void WriteInt32(int offset, int value) => Marshal.WriteInt32((nint)(_control + offset), value);
    private void WriteInt64(int offset, long value) => Marshal.WriteInt64((nint)(_control + offset), value);
    private void WriteString(int offset, string value) => Marshal.Copy(Encoding.Unicode.GetBytes(value + '\0'), 0, (nint)(_control + offset), Math.Min((value.Length + 1) * sizeof(char), NameChars * sizeof(char)));
    private string ReadString(int offset) => Marshal.PtrToStringUni((nint)(_control + offset)) ?? string.Empty;

    private static string DescribeFailure(int value) => value switch
    {
        1 => "Graphics hook protocol mismatch.",
        2 => "Game backbuffer format is unsupported.",
        3 => "Game does not expose a D3D11 swap chain.",
        4 => "Graphics hook could not create shared resources.",
        5 => "Graphics hook D3D11 device failed.",
        6 => "Game process exited.",
        7 => "An incompatible ClypDat hook is already resident; restart the game.",
        _ => "Graphics hook failed."
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WriteInt32(StateOffset, 5);
        DisposeResources();
        _controlView.SafeMemoryMappedViewHandle.ReleasePointer();
        _controlView.Dispose(); _controlMapping.Dispose(); _locatorView.Dispose(); _locatorMapping.Dispose();
    }

    private sealed class ProcessingSlot(ID3D11Texture2D texture) { public ID3D11Texture2D Texture { get; } = texture; public bool Leased { get; set; } }
    private sealed class HookLease(ProcessingSlot slot, long timestamp, long sequence, long generation) : GameFrameLease
    {
        public override ID3D11Texture2D Texture => slot.Texture;
        public override long SourceTimestamp => timestamp;
        public override long AccumulatedPresents => sequence;
        public override int Width => (int)slot.Texture.Description.Width;
        public override int Height => (int)slot.Texture.Description.Height;
        public override long Generation => generation;
        public override bool RequiresCopyBeforeProcessing => false;
        public override void Dispose() => slot.Leased = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
internal readonly record struct GraphicsHookTelemetry(long Presents, long TransportedFrames, long TransportDrops, string? Failure);
