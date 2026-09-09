using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;

namespace ClypDat.Recorder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Configure before loading capture dependencies. Keep WER/dumps enabled.
        const uint failCriticalErrors = 0x0001;
        const uint noOpenFileErrorBox = 0x8000;
        const uint noReportingUi = 0x0020;
        SetErrorMode(GetErrorMode() | failCriticalErrors | noOpenFileErrorBox);
        var reportingResult = WerSetFlags(noReportingUi);
        if (reportingResult < 0) Console.Error.WriteLine($"Recorder WER UI configuration failed: 0x{reportingResult:X8}.");

        var appAssemblyPath = Path.Combine(AppContext.BaseDirectory, "ClypDat.dll");
        if (!File.Exists(appAssemblyPath)) return 2;

        var loadContext = new AppLoadContext(appAssemblyPath);
        var appAssembly = loadContext.LoadFromAssemblyPath(appAssemblyPath);
        var entryPoint = appAssembly.GetType("ClypDat.App.Program", throwOnError: true)!
            .GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ClypDat.App.Program.Main was not found.");

        // Resolve the real entry point even during packaging verification.
        // Invoking it would start the worker's named-pipe loop and block.
        if (args.Contains("--verify-self-contained", StringComparer.Ordinal)) return 0;

        var workerArgs = args.Contains("--capture-worker", StringComparer.OrdinalIgnoreCase)
            ? args
            : [.. args, "--capture-worker"];
        entryPoint.Invoke(null, [workerArgs]);
        return 0;
    }

    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);

    private sealed class AppLoadContext(string appAssemblyPath) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(appAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
