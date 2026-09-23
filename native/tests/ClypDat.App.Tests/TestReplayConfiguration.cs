using ClypDat.App.Services;

namespace ClypDat.App.Tests;

internal static class TestReplayConfiguration
{
    internal static ReplayBufferConfig Create(string root, string container) => new(60, 64, 30, 0, 0, 64, 64,
        "", "", ["Discord"], ["default"], "", [], "Synthetic", "synthetic.exe", "", "", LibraryFolder: root,
        FullSessionRecordingEnabled: true, FullSessionRecordingFolder: Path.Combine(root, "VODs"), FullSessionContainer: container);
}
