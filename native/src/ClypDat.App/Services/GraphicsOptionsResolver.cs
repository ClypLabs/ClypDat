using System.Runtime.InteropServices;
using Avalonia;

namespace ClypDat.App.Services;

internal static class GraphicsOptionsResolver
{
    private static readonly Win32RenderingMode[] DefaultRendering =
    [
        Win32RenderingMode.AngleEgl,
        Win32RenderingMode.Software
    ];

    public static string ActiveDescription { get; private set; } = "not resolved";

    public static Win32PlatformOptions Resolve()
    {
        var requested = Environment.GetEnvironmentVariable("CLYPDAT_GRAPHICS")?.Trim().ToLowerInvariant();
        var isServer = WindowsPlatformProfile.IsServer();
        var mode = requested ?? "auto";

        var (rendering, composition) = mode switch
        {
            "auto" when isServer =>
                (DefaultRendering, new[] { Win32CompositionMode.RedirectionSurface }),
            "auto" =>
                (DefaultRendering, new[]
                {
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface
                }),
            "winui" =>
                (new[] { Win32RenderingMode.AngleEgl }, new[] { Win32CompositionMode.WinUIComposition }),
            "dcomp" =>
                (new[] { Win32RenderingMode.AngleEgl }, new[] { Win32CompositionMode.DirectComposition }),
            "redirection" =>
                (DefaultRendering, new[] { Win32CompositionMode.RedirectionSurface }),
            "software" =>
                (new[] { Win32RenderingMode.Software }, new[] { Win32CompositionMode.RedirectionSurface }),
            _ => ResolveInvalidMode(requested, isServer)
        };

        ActiveDescription =
            $"server={isServer}; CLYPDAT_GRAPHICS={mode}; RenderingMode=[{string.Join(",", rendering)}]; CompositionMode=[{string.Join(",", composition)}]";

        return new Win32PlatformOptions
        {
            RenderingMode = rendering,
            CompositionMode = composition
        };
    }

    private static (Win32RenderingMode[] Rendering, Win32CompositionMode[] Composition) ResolveInvalidMode(string? requested, bool isServer)
    {
        AppLog.Info($"Graphics: ignoring unknown CLYPDAT_GRAPHICS='{requested}', using auto.");
        return isServer
            ? (DefaultRendering, new[] { Win32CompositionMode.RedirectionSurface })
            : (DefaultRendering, new[]
            {
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface
            });
    }
}

internal static class WindowsPlatformProfile
{
    private const byte VerNtWorkstation = 1;

    public static bool IsServer()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var version = new OsVersionInfoEx
        {
            Size = Marshal.SizeOf<OsVersionInfoEx>()
        };

        return RtlGetVersion(ref version) == 0 && version.ProductType != VerNtWorkstation;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfoEx versionInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public int Size;
        public int MajorVersion;
        public int MinorVersion;
        public int BuildNumber;
        public int PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }
}
