using System.Security.Cryptography;
using System.Text;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Keeps libvlc's plugins/plugins.dat current. Without it libvlc loads every
/// plugin DLL (~320, ~96 MB) on each start just to read what it provides; cold,
/// with antivirus scanning each one, that took 10-60s per launch. With the cache
/// it only loads the plugins a clip actually needs.
/// Publish builds the cache (<see cref="Build"/>) and ships a fingerprint of
/// the plugin files beside it, so the first launch after an install or update
/// already has one. An install that rewrites file times (MSI, a zip's 2-second
/// times) no longer matches that fingerprint, and libvlc rewrites the cache here
/// with --reset-plugins-cache; a per-user stamp then records that rebuild.
/// libvlc ignores entries whose size or mtime changed: a stale cache is slow,
/// never wrong.
/// </summary>
internal static class LibVlcPluginCache
{
    private const string ResetOption = "--reset-plugins-cache";
    private const string CacheName = "plugins.dat";
    private const string ShippedStampName = "plugins.dat.fingerprint";

    private static string PluginDirectory => Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins");

    /// <summary>Options for a new LibVLC, plus the fingerprint to commit once it exists (null when the cache is current).</summary>
    public static string[] Options(string[] options, out string? pendingFingerprint)
    {
        pendingFingerprint = null;
        try
        {
            var directory = PluginDirectory;
            if (!Directory.Exists(directory)) return options;
            var fingerprint = Fingerprint(directory);
            if (File.Exists(Path.Combine(directory, CacheName)) &&
                (ReadText(Path.Combine(directory, ShippedStampName)) == fingerprint || ReadText(StampPath(directory)) == fingerprint))
                return options;
            pendingFingerprint = fingerprint;
            return [.. options, ResetOption];
        }
        catch (Exception error)
        {
            AppLog.Error("LibVLC plugin cache check failed; starting without a rebuild.", error);
            return options;
        }
    }

    /// <summary>Records the cache libvlc just wrote. A read-only install leaves no cache and records nothing.</summary>
    public static void Commit(string fingerprint)
    {
        try
        {
            var directory = PluginDirectory;
            if (!File.Exists(Path.Combine(directory, CacheName)))
            {
                AppLog.Info($"LibVLC plugin cache: could not be written to '{directory}'.");
                return;
            }
            Directory.CreateDirectory(AppDataPaths.Root);
            File.WriteAllText(StampPath(directory), fingerprint);
            AppLog.Info("LibVLC plugin cache: rebuilt.");
        }
        catch (Exception error)
        {
            AppLog.Error("LibVLC plugin cache stamp write failed.", error);
        }
    }

    /// <summary>
    /// Publish step (ClypDat.exe --build-libvlc-plugin-cache): writes the cache
    /// for the published plugin files and the fingerprint that vouches for it.
    /// Touches no user data, so it is safe on a build machine.
    /// </summary>
    public static int Build()
    {
        var directory = PluginDirectory;
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"LibVLC plugin directory not found: {directory}");
            return 3;
        }
        File.Delete(Path.Combine(directory, CacheName));
        File.Delete(Path.Combine(directory, ShippedStampName));
        LibVLCSharp.Shared.Core.Initialize();
        using (new LibVLCSharp.Shared.LibVLC("--quiet", ResetOption)) { }
        if (!File.Exists(Path.Combine(directory, CacheName)))
        {
            Console.Error.WriteLine($"libvlc did not write {CacheName} in {directory}");
            return 4;
        }
        File.WriteAllText(Path.Combine(directory, ShippedStampName), Fingerprint(directory));
        return 0;
    }

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    // One stamp per install location, so a dev build and an installed build
    // on the same machine do not keep invalidating each other.
    private static string StampPath(string directory) =>
        Path.Combine(AppDataPaths.Root, $"libvlc-plugins-{Hash(directory.ToUpperInvariant())[..16]}.txt");

    private static string Fingerprint(string directory)
    {
        var listing = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            listing.Append(Path.GetRelativePath(directory, file)).Append('|').Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Hash(listing.ToString());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
