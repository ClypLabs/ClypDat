using System.Globalization;

namespace ClypDat.App.Services;

// starttime (proc stat field 22) prevents a reused PID from inheriting a game.
internal sealed record LinuxProcessIdentity(int Pid, ulong StartTime, string ExecutablePath, int? SteamAppId)
{
    internal static LinuxProcessIdentity? Read(int pid, string procRoot = "/proc")
    {
        if (pid <= 0) return null;
        try
        {
            var root = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture));
            var before = ReadStartTime(File.ReadAllText(Path.Combine(root, "stat")));
            var executable = File.ResolveLinkTarget(Path.Combine(root, "exe"), true)?.FullName;
            if (before is null || string.IsNullOrWhiteSpace(executable)) return null;
            var arguments = File.ReadAllText(Path.Combine(root, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var environment = File.ReadAllText(Path.Combine(root, "environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var cwd = Directory.ResolveLinkTarget(Path.Combine(root, "cwd"), true)?.FullName;
            // Wine's host exe is a loader. Only accept a Windows image backed by
            // a real Unix path; a title or app ID alone is not process evidence.
            if (Path.GetFileName(executable).StartsWith("wine", StringComparison.OrdinalIgnoreCase))
            {
                var image = arguments.FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (image is not null)
                {
                    var candidate = image.Replace('\\', '/');
                    if (candidate.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase)) candidate = candidate[2..];
                    else if (candidate.Length > 2 && candidate[1] == ':')
                    {
                        var prefix = environment.FirstOrDefault(e => e.StartsWith("WINEPREFIX=", StringComparison.Ordinal))?[11..];
                        if (prefix is not null) candidate = Path.Combine(prefix, "dosdevices", candidate[..2].ToLowerInvariant(), candidate[3..]);
                    }
                    else if (!Path.IsPathRooted(candidate) && cwd is not null) candidate = Path.Combine(cwd, candidate);
                    if (Path.IsPathRooted(candidate) && File.Exists(candidate)) executable = Path.GetFullPath(candidate);
                }
            }
            int? appId = null;
            foreach (var key in new[] { "SteamAppId=", "SteamGameId=" })
            {
                var value = environment.FirstOrDefault(e => e.StartsWith(key, StringComparison.Ordinal));
                if (value is not null && int.TryParse(value.AsSpan(key.Length), out var parsed) && parsed > 0) { appId = parsed; break; }
            }
            if (ReadStartTime(File.ReadAllText(Path.Combine(root, "stat"))) != before) return null;
            return new(pid, before.Value, executable, appId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static ulong? ReadStartTime(string stat)
    {
        var end = stat.LastIndexOf(')');
        if (end < 0) return null;
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && ulong.TryParse(fields[19], out var value) ? value : null;
    }
}
