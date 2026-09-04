namespace ClypDat.App.Services;

// Game discovery and capture startup can both finish behind a launcher. Admit
// the hint only once the current recording can actually be seen by the player.
internal sealed class GameRecordingNotificationState
{
    private string _gameKey = string.Empty;
    private int _processId;
    private bool _announced;

    public bool TryAnnounce(GameDetection game, bool recordingReady, bool enabled)
    {
        var key = game.IsDetected
            ? string.IsNullOrWhiteSpace(game.DetectionKey) ? game.ExeName : game.DetectionKey
            : string.Empty;
        if (!string.Equals(key, _gameKey, StringComparison.OrdinalIgnoreCase) || _processId != game.ProcessId || !game.IsDetected)
        {
            _gameKey = key;
            _processId = game.ProcessId;
            _announced = false;
        }
        if (_announced || !enabled || !recordingReady || !game.IsDetected || !game.IsForeground) return false;
        _announced = true;
        return true;
    }
}
