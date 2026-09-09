using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class RawInputRecorderTests
{
    [Fact]
    public void UnstartedRecorderCannotClaimKeyboardWasRecorded()
    {
        using var recorder = new RawInputRecorder();
        var start = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var index = recorder.Snapshot(start, start.AddSeconds(2));
        Assert.NotNull(index.MissingHistory);
    }
}
