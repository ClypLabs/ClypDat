using System.Diagnostics;

namespace ClypDat.App.Services;

public enum StandaloneClassificationKind { RecognizedGame, NeedsReview, ExcludedSoftware, Unknown }

public sealed record StandaloneGameClassification(StandaloneClassificationKind Kind, string DisplayName, string Reason);

// Deliberately narrow. A false positive starts capture; an unknown executable
// must therefore stay unknown until the user explicitly adds it.
public static class StandaloneGameClassifier
{
    private static readonly HashSet<string> ExcludedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamservice.exe", "steamwebhelper.exe", "obs64.exe", "obs32.exe",
        "streamdeck.exe", "elgato*.exe", "epicgameslauncher.exe", "battle.net.exe",
        "updater.exe", "update.exe", "crashreportclient.exe", "crashpad_handler.exe",
        "unitycrashhandler32.exe", "unitycrashhandler64.exe", "robloxcrashhandler.exe",
        "modorganizer.exe", "modorganizer2.exe", "vortex.exe", "nexusclient.exe",
        "helldivers2modmanager.exe", "divamodmanager.exe", "r2modman.exe"
    };

    public static bool IsExcluded(string? path, string? executable)
    {
        var name = executable ?? Path.GetFileName(path ?? string.Empty);
        return ExcludedExecutables.Contains(name) ||
            (name.StartsWith("elgato", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    public static StandaloneGameClassification Classify(string executablePath)
    {
        var exe = Path.GetFileName(executablePath);
        if (IsExcluded(executablePath, exe)) return new(StandaloneClassificationKind.ExcludedSoftware, "", "Known software executable");
        if (!File.Exists(executablePath)) return new(StandaloneClassificationKind.Unknown, "", "Executable unavailable");
        var folder = Path.GetDirectoryName(executablePath) ?? "";
        var product = ReadProductName(executablePath);
        if (exe.Equals("osu!.exe", StringComparison.OrdinalIgnoreCase) || product.Equals("osu!", StringComparison.OrdinalIgnoreCase))
            return new(StandaloneClassificationKind.RecognizedGame, "osu!", "osu! executable product identity");
        if (exe.Equals("osu!.lazer.exe", StringComparison.OrdinalIgnoreCase) || product.Contains("osu!lazer", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(folder, "osu.Game.dll")))
            return new(StandaloneClassificationKind.RecognizedGame, "osu!lazer", "osu!lazer installation files");

        var fnfAssets = Directory.Exists(Path.Combine(folder, "assets")) &&
            (Directory.Exists(Path.Combine(folder, "assets", "songs")) || Directory.Exists(Path.Combine(folder, "assets", "data"))) &&
            (Directory.Exists(Path.Combine(folder, "assets", "characters")) || File.Exists(Path.Combine(folder, "Project.xml")) || File.Exists(Path.Combine(folder, "openfl.xml")));
        var fnfProduct = product.Contains("Friday Night Funkin", StringComparison.OrdinalIgnoreCase) || product.Contains("Psych Engine", StringComparison.OrdinalIgnoreCase);
        if (fnfProduct || fnfAssets)
        {
            var name = fnfProduct && !product.Contains("Psych Engine", StringComparison.OrdinalIgnoreCase)
                ? product : Path.GetFileName(folder);
            return new(StandaloneClassificationKind.RecognizedGame, string.IsNullOrWhiteSpace(name) ? "Friday Night Funkin'" : name, fnfProduct ? "FNF executable product identity" : "FNF packaged charts, characters, and songs");
        }

        // Unity evidence is useful, but never enough for unattended capture.
        if (Directory.Exists(Path.Combine(folder, Path.GetFileNameWithoutExtension(exe) + "_Data")))
            return new(StandaloneClassificationKind.NeedsReview, Path.GetFileNameWithoutExtension(exe), "Unity player data folder");
        return new(StandaloneClassificationKind.Unknown, "", "No game-specific installation evidence");
    }

    private static string ReadProductName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName ?? string.Empty; }
        catch { return string.Empty; }
    }
}

public sealed class StandaloneGameDiscovery
{
    public async Task<IReadOnlyList<(string Path, StandaloneGameClassification Classification)>> ScanAsync(IEnumerable<string> roots, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var result = new List<(string, StandaloneGameClassification)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report(root);
            await Task.Run(() => ScanRoot(root, result, seen, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static void ScanRoot(string root, List<(string, StandaloneGameClassification)> result, HashSet<string> seen, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var installationFound = false;
                foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
                {
                    token.ThrowIfCancellationRequested();
                    var full = Path.GetFullPath(file);
                    if (!seen.Add(full)) continue;
                    var classification = StandaloneGameClassifier.Classify(full);
                    if (classification.Kind is StandaloneClassificationKind.RecognizedGame or StandaloneClassificationKind.NeedsReview)
                    {
                        result.Add((full, classification));
                        installationFound |= classification.Kind == StandaloneClassificationKind.RecognizedGame;
                    }
                }
                if (!installationFound) foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }
}
