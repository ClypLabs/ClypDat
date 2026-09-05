namespace ClypDat.App.Services;

/// <summary>
/// How many clips have been saved for the game currently being played, for the
/// "X clips saved" line in the Discord status.
///
/// The count used to be a bare int that nothing ever zeroed, so it accumulated
/// for the whole app run: one clip in a fresh game showed as "3 clips saved"
/// because two came from titles closed hours earlier.
///
/// It deliberately keys on the game NAME rather than on
/// MainWindowViewModel's activity "kind". The kind is
/// "recording:&lt;game&gt;"/"playing:&lt;game&gt;", so it also changes when the replay
/// buffer starts or stops for the same game - resetting on that would wipe the
/// tally mid-game every time recording toggled.
///
/// Lifted out of the view model because that class is dispatcher-bound and has
/// no test file, so none of this is testable in place - the same reason
/// NewClipsPresentationPolicy lives on its own.
/// </summary>
internal sealed class DiscordClipTally
{
    private string _game = string.Empty;

    public int Count { get; private set; }

    /// <summary>
    /// Point the tally at whatever is being played now, zeroing it when that
    /// differs from what it was counting. Call this on every presence update,
    /// including when nothing is being played: an empty name is what makes
    /// closing a game and relaunching it start from zero rather than resume the
    /// old count.
    /// </summary>
    public void ObserveActivity(string? activityName)
    {
        var game = activityName ?? string.Empty;
        if (string.Equals(game, _game, StringComparison.Ordinal)) return;
        _game = game;
        Count = 0;
    }

    public void Record() => Count++;

    public string Describe() => Count switch
    {
        0 => "Ready to clip",
        1 => "1 clip saved",
        _ => $"{Count:N0} clips saved"
    };
}
