using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed record DetectorHostPolicy(string GameId, IReadOnlyList<string> EnabledEventIds, string PackId, string PackVersion, string PackHash);
internal sealed record DetectorHostMessage(string Type, JsonElement Payload);

internal static class DetectorHostWire
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Payloads MUST be read back with this rather than JsonElement.Deserialize,
    /// which defaults to case-sensitive matching. WriteAsync serializes with web
    /// defaults, so a "gameId" on the wire silently failed to bind to GameId and
    /// every field arrived at its default - an empty policy on the host side and
    /// an empty event on the client side.
    /// </summary>
    public static T? Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(JsonOptions);

    public static async Task WriteAsync(Stream stream, string type, object payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = DetectorHostProtocol.Version, type, payload }, JsonOptions);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<DetectorHostMessage?> ReadAsync(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(stream, lengthBytes, token).ConfigureAwait(false)) return null;
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Invalid detector-host message length.");
        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, token).ConfigureAwait(false)) return null;
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != DetectorHostProtocol.Version)
            throw new InvalidDataException("Unsupported detector-host protocol version.");
        return new DetectorHostMessage(root.GetProperty("type").GetString() ?? string.Empty, root.GetProperty("payload").Clone());
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

internal static class DetectorFrameCodec
{
    // Overwatch's left column is a tall strip (~500x864 = 431 KB on its own)
    // because the Play of the Game banner moves vertically, so the HELLDIVERS-era
    // 256 KB budget no longer covers a frame. Three slots of this size is 3 MB
    // of shared memory, which is nothing next to the 512 MB the host is capped at.
    internal const int SlotBytes = 1024 * 1024;

    public static void Write(MemoryMappedViewAccessor view, int slot, DetectorFrameSnapshot frame)
    {
        var offset = (long)slot * SlotBytes;
        view.Write(offset, frame.CapturedUtc.Ticks); offset += 8;
        foreach (var image in new[] { frame.First, frame.Second, frame.Third })
        {
            view.Write(offset, image.Width); offset += 4;
            view.Write(offset, image.Height); offset += 4;
            view.Write(offset, image.Pixels.Length); offset += 4;
            if (offset + image.Pixels.Length > (long)(slot + 1) * SlotBytes)
                throw new InvalidDataException("Detector frame exceeds its shared-memory slot.");
            view.WriteArray(offset, image.Pixels, 0, image.Pixels.Length); offset += image.Pixels.Length;
        }
    }

    public static DetectorFrameSnapshot Read(MemoryMappedViewAccessor view, int slot)
    {
        if (slot is < 0 or >= DetectorHostProtocol.FrameSlotCount) throw new InvalidDataException("Detector frame slot is invalid.");
        var offset = (long)slot * SlotBytes;
        var timestamp = new DateTime(view.ReadInt64(offset), DateTimeKind.Utc); offset += 8;
        var images = new GrayDetectorImage[3];
        for (var index = 0; index < images.Length; index++)
        {
            var width = view.ReadInt32(offset); offset += 4;
            var height = view.ReadInt32(offset); offset += 4;
            var length = view.ReadInt32(offset); offset += 4;
            if (width <= 0 || height <= 0 || length != checked(width * height) || offset + length > (long)(slot + 1) * SlotBytes)
                throw new InvalidDataException("Detector shared-memory image is invalid.");
            var pixels = new byte[length];
            view.ReadArray(offset, pixels, 0, length); offset += length;
            images[index] = new GrayDetectorImage(width, height, pixels);
        }
        return new DetectorFrameSnapshot(timestamp, images[0], images[1], images[2]);
    }
}

internal sealed class DetectorCrashCircuitBreaker
{
    private readonly Queue<DateTime> _crashes = new();
    public int Record(DateTime now)
    {
        while (_crashes.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(10)) _crashes.Dequeue();
        _crashes.Enqueue(now);
        return _crashes.Count;
    }
}

internal sealed class DetectorHostClient : IAsyncDisposable
{
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private readonly string _mapName;
    private readonly string _pipeName;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Channel<DetectorFrameSnapshot> _frames = Channel.CreateBounded<DetectorFrameSnapshot>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly DetectorCrashCircuitBreaker _breaker = new();
    private readonly Task _publisher;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private DetectorProcessJob? _job;
    private DetectorHostPolicy? _policy;
    private int _slot;
    private int _generation;
    private bool _quarantined;

    public DetectorHostClient()
    {
        _mapName = DetectorHostProtocol.SharedMemoryPrefix + _nonce;
        _pipeName = DetectorHostProtocol.PipePrefix + _nonce;
        _map = MemoryMappedFile.CreateNew(_mapName, (long)DetectorFrameCodec.SlotBytes * DetectorHostProtocol.FrameSlotCount,
            MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor();
        _publisher = Task.Run(PublishAsync);
    }

    public event EventHandler<AutoClipDetectorEvent>? Detected;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? Quarantined;

    public async Task StartAsync(DetectorHostPolicy policy, CancellationToken token)
    {
        _policy = policy;
        if (_quarantined) throw new InvalidOperationException("Detector pack is quarantined after repeated crashes.");
        await ConnectAsync(token).ConfigureAwait(false);
        await SendAsync("policy", policy, token).ConfigureAwait(false);
        StatusChanged?.Invoke(this, $"Watching — pack {policy.PackVersion}");
    }

    public void Offer(DetectorFrameSnapshot frame)
    {
        if (!_quarantined && _pipe?.IsConnected == true) _frames.Writer.TryWrite(frame);
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (_pipe?.IsConnected == true) return;
        await _connectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_pipe?.IsConnected == true) return;
            _pipe?.Dispose();
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipe = pipe;
            var executable = DetectorHostExecutable.Resolve(Environment.ProcessPath ?? string.Empty);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--detector-host", "--pipe", _pipeName, "--frames", _mapName }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Detector host did not start.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _job?.Dispose();
            _job = DetectorProcessJob.Attach(process);
            _process = process;
            var generation = Interlocked.Increment(ref _generation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            _ = Task.Run(() => ReadAsync(pipe, generation));
        }
        finally { _connectGate.Release(); }
    }

    private async Task PublishAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_shutdown.Token))
            {
                var pipe = _pipe;
                if (pipe?.IsConnected != true) continue;
                var slot = _slot++ % DetectorHostProtocol.FrameSlotCount;
                DetectorFrameCodec.Write(_view, slot, frame);
                try { await SendAsync("frame", new { slot }, _shutdown.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ReadAsync(Stream pipe, int generation)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested && await DetectorHostWire.ReadAsync(pipe, _shutdown.Token).ConfigureAwait(false) is { } message)
            {
                if (message.Type == "detected" && DetectorHostWire.Deserialize<AutoClipDetectorEvent>(message.Payload) is { } detected)
                    Detected?.Invoke(this, detected);
                else if (message.Type == "status") StatusChanged?.Invoke(this, message.Payload.GetString() ?? "Degraded");
            }
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_shutdown.IsCancellationRequested) CaptureWorkerLog.Info($"Detector host connection lost: {error.Message}");
        }
        finally
        {
            if (!_shutdown.IsCancellationRequested && generation == Volatile.Read(ref _generation)) _ = RecoverAsync();
        }
    }

    private async Task RecoverAsync()
    {
        var count = _breaker.Record(DateTime.UtcNow);
        if (count >= 3)
        {
            _quarantined = true;
            StatusChanged?.Invoke(this, "Failed — detector pack quarantined after repeated crashes");
            Quarantined?.Invoke(this, "three crashes within ten minutes");
            return;
        }
        StatusChanged?.Invoke(this, "Recovering — detector host restarting");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(count), _shutdown.Token).ConfigureAwait(false);
            await ConnectAsync(_shutdown.Token).ConfigureAwait(false);
            if (_policy is not null) await SendAsync("policy", _policy, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { CaptureWorkerLog.Error("Detector host recovery failed.", error); }
    }

    private async Task SendAsync(string type, object payload, CancellationToken token)
    {
        var pipe = _pipe ?? throw new IOException("Detector host is disconnected.");
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await DetectorHostWire.WriteAsync(pipe, type, payload, token).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _frames.Writer.TryComplete();
        try { if (_pipe?.IsConnected == true) await SendAsync("shutdown", new { }, CancellationToken.None).ConfigureAwait(false); } catch { }
        try { await _publisher.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        _process?.Dispose(); _pipe?.Dispose(); _job?.Dispose(); _view.Dispose(); _map.Dispose();
        _connectGate.Dispose(); _writeGate.Dispose(); _shutdown.Dispose();
    }
}

internal static class DetectorHostRuntime
{
    public static int Run(string[] args)
    {
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error) { CaptureWorkerLog.Error("Detector host failed.", error); return 1; }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var pipeName = Value(args, "--pipe");
        var mapName = Value(args, "--frames");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000).ConfigureAwait(false);
        using var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        // Which detector runs is the policy's call - the host is a sandbox,
        // not a HELLDIVERS-specific process.
        ILiveGameDetector? detector = null;
        var writeGate = new SemaphoreSlim(1, 1);
        while (await DetectorHostWire.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is { } message)
        {
            switch (message.Type)
            {
                case "policy":
                    var policy = DetectorHostWire.Deserialize<DetectorHostPolicy>(message.Payload) ?? throw new InvalidDataException("Detector policy is invalid.");
                    if (detector is not null) await detector.DisposeAsync().ConfigureAwait(false);
                    detector = CreateDetector(policy.GameId);
                    if (detector is null)
                    {
                        await Send("status", $"Failed - no detector for '{policy.GameId}'").ConfigureAwait(false);
                        return 1;
                    }
                    detector.Detected += (_, detected) => _ = Send("detected", detected);
                    detector.StatusChanged += (_, status) => _ = Send("status", status);
                    detector.ApplyPolicy(true, policy.EnabledEventIds);
                    break;
                case "frame":
                    detector?.Offer(DetectorFrameCodec.Read(view, message.Payload.GetProperty("slot").GetInt32()));
                    break;
                case "shutdown":
                    if (detector is not null) await detector.DisposeAsync().ConfigureAwait(false);
                    return 0;
            }
        }
        if (detector is not null) await detector.DisposeAsync().ConfigureAwait(false);
        return 0;

        static ILiveGameDetector? CreateDetector(string gameId) => gameId?.ToLowerInvariant() switch
        {
            "helldivers2" => new LiveHelldivers2Detector(),
            "overwatch" => new LiveOverwatchDetector(),
            "fortnite" => new LiveFortniteDetector(),
            _ => null
        };

        async Task Send(string type, object payload)
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try { await DetectorHostWire.WriteAsync(pipe, type, payload, CancellationToken.None).ConfigureAwait(false); }
            catch { }
            finally { writeGate.Release(); }
        }
    }

    private static string Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {name}.");
    }
}

internal static class DetectorHostExecutable
{
    internal const string FileName = "ClypDatDetectorHost.exe";
    public static string Resolve(string appPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var sibling = Path.Combine(Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory, FileName);
        return exists(sibling) ? sibling : appPath;
    }
}

internal sealed class DetectorProcessJob : IDisposable
{
    private readonly nint _handle;
    private DetectorProcessJob(nint handle) => _handle = handle;
    public static DetectorProcessJob Attach(Process process)
    {
        var handle = CreateJobObject(nint.Zero, null);
        if (handle == 0) throw new System.ComponentModel.Win32Exception();
        var limits = new JobExtendedLimitInformation
        {
            BasicLimitInformation = new JobBasicLimitInformation
            {
                LimitFlags = 0x00002000 | 0x00000100 | 0x00000008,
                ActiveProcessLimit = 1
            },
            ProcessMemoryLimit = (nuint)DetectorHostProtocol.MaximumWorkingSetBytes
        };
        var size = Marshal.SizeOf<JobExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)size) || !AssignProcessToJobObject(handle, process.Handle))
                throw new System.ComponentModel.Win32Exception();
            return new DetectorProcessJob(handle);
        }
        catch { CloseHandle(handle); throw; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimitInformation { public JobBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
