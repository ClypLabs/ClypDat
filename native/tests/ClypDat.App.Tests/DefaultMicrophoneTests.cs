using ClypDat.App.Services;
using NAudio.CoreAudioApi;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DefaultMicrophoneTests
{
    [Fact]
    public void DefaultResolution_UsesMultimediaRole()
    {
        Assert.Equal(Role.Multimedia, DefaultMicrophone.Role);
    }

    [Theory]
    [InlineData(DataFlow.Capture, Role.Console, true)]
    [InlineData(DataFlow.Capture, Role.Multimedia, true)]
    [InlineData(DataFlow.Capture, Role.Communications, false)]
    [InlineData(DataFlow.Render, Role.Console, false)]
    [InlineData(DataFlow.Render, Role.Multimedia, false)]
    public void DefaultChangeWatcher_OnlyHandlesCaptureConsoleOrMultimedia(DataFlow flow, Role role, bool expected)
    {
        Assert.Equal(expected, DefaultMicrophoneWatcher.IsRelevantDefaultChange(flow, role));
    }
}
