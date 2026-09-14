using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public sealed partial class ForegroundGameDetector
{
    private GameDetection DetectLinux() => SelectLinuxGame(ScanLinuxWindows());

    internal GameDetection SelectLinuxGame(IReadOnlyList<GameDetection> games)
    {
        var foreground = games.FirstOrDefault(g => g.IsForeground);
        // Match a live UUID and process incarnation. Minimize/focus loss does
        // not select another application or discard the remembered source.
        var previous = games.FirstOrDefault(g => g.LinuxTarget == _lastGame.LinuxTarget &&
            g.ProcessId == _lastGame.ProcessId && g.LinuxProcessStartTime == _lastGame.LinuxProcessStartTime);
        return _lastGame = foreground ?? previous ?? games.FirstOrDefault() ?? GameDetection.None;
    }

    private IReadOnlyList<GameDetection> ScanLinuxWindows() => KdeWindowMonitor.Shared.Snapshot
        .OrderByDescending(w => (long)w.Width * w.Height)
        .Select(w => MatchLinuxWindow(w, LinuxProcessIdentity.Read(w.Pid)))
        .Where(g => g.IsDetected).ToArray();

    internal GameDetection MatchLinuxWindow(KdeWindowMetadata window, LinuxProcessIdentity? process)
    {
        if (process is null || process.Pid != window.Pid || process.Pid == Environment.ProcessId ||
            !Guid.TryParse(window.Uuid, out _) || window.Event == "removed") return GameDetection.None;
        var exe = Path.GetFileName(process.ExecutablePath);
        if (IsIgnored(exe) || IsSoftware(process.ExecutablePath, exe) ||
            process.SteamAppId is { } id && _steamGames.Snapshot.Classify(id) == SteamAppKind.NonGame)
            return GameDetection.None;
        var game = MatchWindow(process.ExecutablePath, exe, window.Title, window.AppId,
            window.Width, window.Height, processId: process.Pid);
        // Proton loaders can live outside steamapps/common. Environment ID is
        // corroborated by Steam's installed-game classification, never title.
        if (!game.IsDetected && process.SteamAppId is { } appId &&
            _steamGames.Snapshot.FindGameByAppId(appId) is { } install)
            game = new(install.DisplayName, exe, window.Title, window.AppId, 0, process.Pid, true,
                MatchSource: GameMatchSource.Steam, DetectionKey: $"steam-{appId}");
        if (!game.IsDetected || IsIgnored(game.DetectionKey)) return GameDetection.None;
        return game with
        {
            WindowHandle = 0,
            LinuxTarget = new(LinuxCaptureTargetKind.KdeWindow, window.Uuid),
            LinuxProcessStartTime = process.StartTime,
            IsForeground = window.Focused && !window.Minimized
        };
    }
}
