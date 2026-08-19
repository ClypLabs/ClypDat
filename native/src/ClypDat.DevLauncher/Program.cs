using System.Diagnostics;
using ClypDat.DevChannel;

namespace ClypDat.DevLauncher;

internal static class Program
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);

    public static int Main()
    {
        var installRoot = AppContext.BaseDirectory;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = Path.Combine(localAppData, "ClypDat-Dev");
        var versionsRoot = Path.Combine(installRoot, "versions");
        var statePath = Path.Combine(dataRoot, "state.json");
        var healthRoot = Path.Combine(dataRoot, "health");
        Directory.CreateDirectory(versionsRoot);
        Directory.CreateDirectory(healthRoot);

        var state = DevInstallStateStore.Load(statePath);
        state = ActivatePending(state, statePath, versionsRoot);
        var current = FindUsableBuild(state.CurrentBuildId, versionsRoot);
        if (current is null)
        {
            current = Directory.EnumerateDirectories(versionsRoot)
                .Where(path => File.Exists(Path.Combine(path, "ClypDat.exe")))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (current is null) return 1;
            state = state with { CurrentBuildId = Path.GetFileName(current) };
            DevInstallStateStore.SaveAtomic(statePath, state);
        }

        var buildId = Path.GetFileName(current);
        var token = Guid.NewGuid().ToString("N");
        var healthPath = Path.Combine(healthRoot, token + ".ok");
        TryDelete(healthPath);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(current, "ClypDat.exe"))
        {
            WorkingDirectory = current,
            UseShellExecute = false,
            Arguments = $"--dev-health-token={token} --dev-build-id={buildId}"
        });
        if (process is null) return RollBack(state, statePath, versionsRoot, healthPath);

        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline && !File.Exists(healthPath))
        {
            if (process.HasExited) return RollBack(state, statePath, versionsRoot, healthPath);
            Thread.Sleep(100);
        }

        if (!File.Exists(healthPath))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(5000);
            return RollBack(state, statePath, versionsRoot, healthPath);
        }

        state = state with { CurrentBuildId = buildId };
        DevInstallStateStore.SaveAtomic(statePath, state);
        CleanupOldVersions(state, versionsRoot);
        TryDelete(healthPath);
        return 0;
    }

    private static DevInstallState ActivatePending(DevInstallState state, string statePath, string versionsRoot)
    {
        if (state.PendingBuildId is null || FindUsableBuild(state.PendingBuildId, versionsRoot) is null) return state;
        var next = state with { CurrentBuildId = state.PendingBuildId, PreviousBuildId = state.CurrentBuildId, PendingBuildId = null };
        DevInstallStateStore.SaveAtomic(statePath, next);
        return next;
    }

    private static int RollBack(DevInstallState state, string statePath, string versionsRoot, string healthPath)
    {
        TryDelete(healthPath);
        var previous = FindUsableBuild(state.PreviousBuildId, versionsRoot);
        if (previous is null) return 1;
        var restored = state with { CurrentBuildId = Path.GetFileName(previous), PendingBuildId = null };
        DevInstallStateStore.SaveAtomic(statePath, restored);
        using var fallback = Process.Start(new ProcessStartInfo(Path.Combine(previous, "ClypDat.exe"))
        {
            WorkingDirectory = previous,
            UseShellExecute = false,
            Arguments = "--dev-rollback"
        });
        return fallback is null ? 1 : 0;
    }

    private static string? FindUsableBuild(string? buildId, string versionsRoot) =>
        buildId is null ? null :
        buildId.IndexOfAny(['/', '\\', ':']) >= 0 ? null :
        Directory.Exists(Path.Combine(versionsRoot, buildId)) && File.Exists(Path.Combine(versionsRoot, buildId, "ClypDat.exe"))
            ? Path.Combine(versionsRoot, buildId) : null;

    private static void CleanupOldVersions(DevInstallState state, string versionsRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
        {
            var id = Path.GetFileName(directory);
            if (id is not null && id != state.CurrentBuildId && id != state.PreviousBuildId && id != state.PendingBuildId)
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
