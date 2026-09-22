using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class RecordingAudioProcessPolicyTests
{
    [Theory]
    [InlineData("ClypDatRecorder")]
    [InlineData("ClypDatRecorder.exe")]
    [InlineData("clypdatrecorder.EXE")]
    public void Recorder_IsNeverEligible(string processName) =>
        Assert.False(RecordingAudioProcessPolicy.IsEligible(processName));

    // Voicemeeter carries every stream on the machine, so recording it as a
    // track duplicates the game and microphone tracks into one mix. SignalRGB
    // holds a session to read levels for lighting and plays nothing at all.
    [Theory]
    [InlineData("voicemeeter", false)]
    [InlineData("voicemeeter8x64.exe", false)]
    [InlineData("VoicemeeterPro.exe", false)]
    [InlineData("SignalRgb.exe", false)]
    [InlineData("SIGNALRGB", false)]
    [InlineData("Discord.exe", true)]
    [InlineData("Voice.exe", true)]
    [InlineData("Signal.exe", true)]
    public void SessionsThatAreNotAppsPlayingSound_AreNeverEligible(string processName, bool expected) =>
        Assert.Equal(expected, RecordingAudioProcessPolicy.IsEligible(processName));

    [Fact]
    public void Filter_RemovesRecorderButKeepsUserApps()
    {
        var filtered = RecordingAudioProcessPolicy.Filter(new Dictionary<string, int>
        {
            ["ClypDatRecorder.exe"] = 100,
            ["Discord.exe"] = 80
        });

        Assert.DoesNotContain(filtered.Keys, key => key.Contains("ClypDatRecorder", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(80, filtered["Discord.exe"]);
    }
}
