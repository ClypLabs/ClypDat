using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Versioned duplex control for the optional D3D11 game hook.  Its per-frame
// path never uses the pipe: Present writes a keyed-mutex shared texture,
// updates a small shared header, then signals a named event.  The pipe is only
// used for startup, resize generations, target-rate control and shutdown.
internal sealed class GameHookSession : IGameFrameSource, IDisposable
{
    internal const string EnableVariable = "CLYPDAT_ENABLE_GAME_HOOK";
    private const uint ProcessCreateThread = 0x0002, ProcessVmOperation = 0x0008, ProcessVmRead = 0x0010, ProcessVmWrite = 0x0020, ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04, WaitObject0 = 0, WaitTimeout = 258;
    private const int HeaderMagic = 0x48444743, HeaderVersion = 1, SurfaceCount = 3;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reader;
    private readonly int _processId;
    private readonly ID3D11Device _device;
    private readonly object _d3dLock;
    private readonly object _stateLock = new();
    private EventWaitHandle? _frameEvent;
    private MemoryMappedFile? _headerMapping;
    private MemoryMappedViewAccessor? _header;
    private ID3D11Texture2D[]? _surfaces;
    private IDXGIKeyedMutex[]? _mutexes;
    private ID3D11Texture2D? _latestTexture;
    private int _generation;
    private long _lastSequence;
    private long _presents;
    private long _transported;
    private long _drops;
    private string? _failure;
    private bool _attached;
    private bool _disposed;

    private GameHookSession(int processId, ID3D11Device device, object d3dLock, int frameRate)
    {
        _processId = processId; _device = device; _d3dLock = d3dLock;
        _pipe = new NamedPipeServerStream(PipeName(processId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _writer = new StreamWriter(_pipe, Encoding.Unicode, bufferSize: 256, leaveOpen: true) { AutoFlush = true };
        _reader = Task.Run(ReadLoopAsync);
        Inject();
        SetTargetFrameRate(frameRate);
    }

    public bool IsAttached { get { lock (_stateLock) return _attached; } }
    public string CaptureMode => "Game Hook (D3D11)";
    public bool HasValidFrames => Interlocked.Read(ref _transported) > 0;
    public string? Failure { get { lock (_stateLock) return _failure; } }
    public GameHookTelemetry Telemetry => new(Interlocked.Read(ref _presents), Interlocked.Read(ref _transported), Interlocked.Read(ref _drops), Failure);

    public static GameHookSession? TryStart(nint windowHandle, ID3D11Device device, object d3dLock, int frameRate, bool automatic)
    {
        if (!automatic && !string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)) return null;
        if (windowHandle == 0) return null;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0) return null;
        try
        {
            var session = new GameHookSession(unchecked((int)processId), device, d3dLock, frameRate);
            AppLog.Info($"Game hook: {(automatic ? "automatic" : "diagnostic")} injection requested for pid={processId}.");
            return session;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game hook: injection setup failed for pid={processId}.", error);
            return null;
        }
    }

    public void SetTargetFrameRate(int frameRate)
    {
        try { _writer.WriteLine($"target {Math.Clamp(frameRate, 30, 144)}"); }
        catch (Exception error) { Fail($"control write failed: {error.Message}"); }
    }

    public bool WaitAndTakeLatestTexture(TimeSpan timeout, CancellationToken token, out ID3D11Texture2D? texture)
    {
        texture = null;
        EventWaitHandle? signal;
        lock (_stateLock) signal = _frameEvent;
        if (signal is null || !signal.WaitOne(timeout)) return false;
        token.ThrowIfCancellationRequested();
        lock (_d3dLock)
        lock (_stateLock)
        {
            if (_disposed || _header is null || _surfaces is null || _mutexes is null) return false;
            if (_header.ReadInt32(0) != HeaderMagic || _header.ReadInt16(4) != HeaderVersion) { Fail("invalid shared transport header"); return false; }
            var generation = _header.ReadInt32(8);
            var slot = _header.ReadInt32(40);
            var sequence = _header.ReadInt64(32);
            if (generation != _generation || slot < 0 || slot >= SurfaceCount || sequence <= _lastSequence) return false;
            try { _mutexes[slot].AcquireSync(1, 0); }
            catch { Interlocked.Increment(ref _drops); return false; }
            try
            {
                var latest = _latestTexture;
                if (latest is null) return false;
                _device.ImmediateContext.CopyResource(latest, _surfaces[slot]);
                _lastSequence = sequence;
                Interlocked.Increment(ref _transported);
                _presents = _header.ReadInt64(48);
                _drops = _header.ReadInt64(64);
                texture = latest.QueryInterface<ID3D11Texture2D>();
                return true;
            }
            finally { _mutexes[slot].ReleaseSync(0); }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            using var reader = new StreamReader(_pipe, Encoding.Unicode, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (!_stopping.IsCancellationRequested)
            {
                var message = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
                if (message is null) { Fail("hook control pipe disconnected"); return; }
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fail($"hook control pipe failed: {error.Message}"); }
    }

    private void HandleMessage(string message)
    {
        var fields = message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 0) return;
        if (fields[0] == "hello" && fields.Length >= 3 && fields[1] == HeaderVersion.ToString() && fields[2] == "attached") { lock (_stateLock) _attached = true; return; }
        if (fields[0] == "surface" && fields.Length == 10 && TryOpenGeneration(fields)) return;
        if (fields[0] == "failed") { Fail(string.Join(' ', fields.Skip(1))); return; }
        if (fields[0] == "stopped") { Fail("hook stopped"); return; }
        AppLog.Info($"Game hook: pid={_processId}, {message}.");
    }

    private bool TryOpenGeneration(string[] fields)
    {
        if (!int.TryParse(fields[1], out var generation) || !int.TryParse(fields[2], out var width) || !int.TryParse(fields[3], out var height) ||
            !int.TryParse(fields[4], out var format) || !int.TryParse(fields[5], out var adapterHigh) || !uint.TryParse(fields[6], out var adapterLow) ||
            !long.TryParse(fields[7], out var first) || !long.TryParse(fields[8], out var second) || !long.TryParse(fields[9], out var third) ||
            width < 1 || height < 1 || generation < 1 || format is not ((int)Format.B8G8R8A8_UNorm or (int)Format.B8G8R8A8_UNorm_SRgb or (int)Format.R8G8B8A8_UNorm or (int)Format.R8G8B8A8_UNorm_SRgb))
        {
            Fail("invalid hook surface metadata"); return false;
        }
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            dxgiDevice.GetAdapter(out var adapter);
            using (adapter)
            {
                using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
                var luid = adapter1.Description1.Luid;
                if (luid.HighPart != adapterHigh || luid.LowPart != adapterLow)
                {
                    Fail("hook transport adapter does not match capture device"); return false;
                }
            }
            using var mapping = MemoryMappedFile.OpenExisting(HeaderName(_processId), MemoryMappedFileRights.Read);
            using var header = mapping.CreateViewAccessor(0, 72, MemoryMappedFileAccess.Read);
            if (header.ReadInt32(0) != HeaderMagic || header.ReadInt16(4) != HeaderVersion || header.ReadInt32(8) != generation || header.ReadInt32(12) != width || header.ReadInt32(16) != height || header.ReadInt32(20) != format)
            { Fail("hook surface/header validation failed"); return false; }
            var opened = new[] { _device.OpenSharedResource<ID3D11Texture2D>((nint)first), _device.OpenSharedResource<ID3D11Texture2D>((nint)second), _device.OpenSharedResource<ID3D11Texture2D>((nint)third) };
            var mutexes = opened.Select(surface => surface.QueryInterface<IDXGIKeyedMutex>()).ToArray();
            var latest = _device.CreateTexture2D(new Texture2DDescription { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = (Format)format, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default });
            var eventHandle = EventWaitHandle.OpenExisting(EventName(_processId));
            lock (_stateLock)
            {
                DisposeTransport();
                _headerMapping = MemoryMappedFile.OpenExisting(HeaderName(_processId), MemoryMappedFileRights.Read);
                _header = _headerMapping.CreateViewAccessor(0, 72, MemoryMappedFileAccess.Read);
                _frameEvent = eventHandle; _surfaces = opened; _mutexes = mutexes; _latestTexture = latest; _generation = generation; _lastSequence = 0;
            }
            AppLog.Info($"Game hook: opened D3D11 transport generation {generation} ({width}x{height}).");
            return true;
        }
        catch (Exception error) { Fail($"could not open hook transport: {error.Message}"); return false; }
    }

    private void Fail(string failure) { lock (_stateLock) _failure ??= failure; AppLog.Info($"Game hook: pid={_processId}, {failure}."); }
    private static string PipeName(int processId) => $"ClypDat-GameHook-{processId}";
    private static string HeaderName(int processId) => $"Local\\ClypDat-GameHook-Header-{processId}";
    private static string EventName(int processId) => $"Local\\ClypDat-GameHook-Frame-{processId}";

    private void Inject()
    {
        var hookPath = Path.Combine(AppContext.BaseDirectory, "ClypDat.GameHook.dll"); if (!File.Exists(hookPath)) throw new FileNotFoundException("Game hook DLL was not published.", hookPath);
        var process = OpenProcess(ProcessCreateThread | ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, _processId); if (process == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not open target process.");
        try
        {
            var pathBytes = Encoding.Unicode.GetBytes(hookPath + '\0'); var remotePath = VirtualAllocEx(process, 0, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite); if (remotePath == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not allocate remote memory.");
            try
            {
                if (!WriteProcessMemory(process, remotePath, pathBytes, pathBytes.Length, out var written) || written != (nint)pathBytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not write remote memory.");
                var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW"); var thread = CreateRemoteThread(process, 0, 0, loadLibrary, remotePath, 0, out _); if (thread == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not start remote loader.");
                try { var wait = WaitForSingleObject(thread, 5_000); if (wait == WaitTimeout) throw new TimeoutException("Game hook remote loader timed out."); if (wait != WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook remote loader wait failed."); if (!GetExitCodeThread(thread, out var module) || module == 0) throw new InvalidOperationException("Game hook remote loader did not return a module handle."); }
                finally { CloseHandle(thread); }
            }
            finally { VirtualFreeEx(process, remotePath, 0, MemRelease); }
        }
        finally { CloseHandle(process); }
    }

    private void DisposeTransport()
    {
        _frameEvent?.Dispose(); _frameEvent = null; _header?.Dispose(); _header = null; _headerMapping?.Dispose(); _headerMapping = null;
        if (_mutexes is not null) foreach (var mutex in _mutexes) mutex.Dispose(); _mutexes = null;
        if (_surfaces is not null) foreach (var surface in _surfaces) surface.Dispose(); _surfaces = null;
        _latestTexture?.Dispose(); _latestTexture = null;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _writer.WriteLine("shutdown"); } catch { }
        _stopping.Cancel(); _pipe.Dispose(); try { _reader.GetAwaiter().GetResult(); } catch { }
        lock (_d3dLock) lock (_stateLock) DisposeTransport(); _writer.Dispose(); _stopping.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out nint written);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetModuleHandle(string moduleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint module, string procedureName);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint creationFlags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(nint thread, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal readonly record struct GameHookTelemetry(long Presents, long TransportedFrames, long TransportDrops, string? Failure);
