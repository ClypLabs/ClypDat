using System.Reflection;
using System.Runtime.Loader;

namespace ClypDat.DetectorHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var appAssemblyPath = Path.Combine(AppContext.BaseDirectory, "ClypDat.dll");
        if (!File.Exists(appAssemblyPath)) return 2;
        var loadContext = new AppLoadContext(appAssemblyPath);
        var appAssembly = loadContext.LoadFromAssemblyPath(appAssemblyPath);
        var entryPoint = appAssembly.GetType("ClypDat.App.Program", true)!
            .GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ClypDat.App.Program.Main was not found.");
        if (args.Contains("--verify-self-contained", StringComparer.Ordinal)) return 0;
        var hostArgs = args.Contains("--detector-host", StringComparer.OrdinalIgnoreCase)
            ? args
            : [.. args, "--detector-host"];
        entryPoint.Invoke(null, [hostArgs]);
        return 0;
    }

    private sealed class AppLoadContext(string appAssemblyPath) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(appAssemblyPath);
        protected override Assembly? Load(AssemblyName name) => _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        protected override nint LoadUnmanagedDll(string name) => _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
