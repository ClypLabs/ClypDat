namespace Eve.Core.Settings;

public sealed class AppSettings
{
    public string LibraryFolder { get; set; } = string.Empty;
    public int ReplayDurationSeconds { get; set; } = 60;
    public string SaveReplayHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool EnableEditorKeyboardShortcuts { get; set; } = true;
}
