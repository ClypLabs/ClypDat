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
