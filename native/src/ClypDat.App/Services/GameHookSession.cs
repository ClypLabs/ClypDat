using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

// Owns test-only injection and hook telemetry. The replay buffer stays on its
// existing capture source until this module also transports frames.
internal sealed class GameHookSession : IDisposable
{
    internal const string EnableVariable = "CLYPDAT_ENABLE_GAME_HOOK";
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reader;
    private readonly int _processId;
    private bool _disposed;

    private GameHookSession(int processId)
    {
        _processId = processId;
        _pipe = new NamedPipeServerStream(PipeName(processId), PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _reader = Task.Run(ReadLoopAsync);
        Inject();
    }

    public static GameHookSession? TryStart(nint windowHandle)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)) return null;
        if (windowHandle == 0) return null;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0) return null;

        try
        {
            var session = new GameHookSession(unchecked((int)processId));
            AppLog.Info($"Game hook: injection requested for pid={processId}.");
            return session;
        }
        catch (Exception error)
        {
            AppLog.Error($"Game hook: injection setup failed for pid={processId}.", error);
            return null;
        }
    }

    private static string PipeName(int processId) => $"ClypDat-GameHook-{processId}";

    private async Task ReadLoopAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            using var reader = new StreamReader(_pipe, Encoding.Unicode, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (!_stopping.IsCancellationRequested)
            {
                var message = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
                if (message is null) return;
                AppLog.Info($"Game hook: pid={_processId}, {message}.");
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException error) { AppLog.Error($"Game hook: pipe failed for pid={_processId}.", error); }
        catch (Exception error) { AppLog.Error($"Game hook: reader failed for pid={_processId}.", error); }
    }

    private void Inject()
    {
        var hookPath = Path.Combine(AppContext.BaseDirectory, "ClypDat.GameHook.dll");
        if (!File.Exists(hookPath)) throw new FileNotFoundException("Game hook DLL was not published.", hookPath);

        var process = OpenProcess(ProcessCreateThread | ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, _processId);
        if (process == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not open target process.");
        try
        {
            var pathBytes = Encoding.Unicode.GetBytes(hookPath + '\0');
            var remotePath = VirtualAllocEx(process, 0, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remotePath == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not allocate remote memory.");
            var releasePath = true;
            try
            {
                if (!WriteProcessMemory(process, remotePath, pathBytes, pathBytes.Length, out var written) || written != (nint)pathBytes.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not write remote memory.");
                var kernel = GetModuleHandle("kernel32.dll");
                var loadLibrary = GetProcAddress(kernel, "LoadLibraryW");
                if (loadLibrary == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not find LoadLibraryW.");
                var thread = CreateRemoteThread(process, 0, 0, loadLibrary, remotePath, 0, out _);
                if (thread == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook could not start remote loader.");
                try
                {
                    var wait = WaitForSingleObject(thread, 5_000);
                    if (wait == WaitTimeout) { releasePath = false; throw new TimeoutException("Game hook remote loader timed out."); }
                    if (wait != WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game hook remote loader wait failed.");
                    if (!GetExitCodeThread(thread, out var moduleHandle) || moduleHandle == 0)
                        throw new InvalidOperationException("Game hook remote loader did not return a module handle.");
                }
                finally { CloseHandle(thread); }
            }
            finally
            {
                if (releasePath) VirtualFreeEx(process, remotePath, 0, MemRelease);
            }
        }
        finally { CloseHandle(process); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        _pipe.Dispose();
        try { _reader.GetAwaiter().GetResult(); } catch { }
        _stopping.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out nint written);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(nint thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
