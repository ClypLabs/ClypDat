using System.Reflection;
using System.Runtime.Loader;

namespace ClypDat.Recorder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
