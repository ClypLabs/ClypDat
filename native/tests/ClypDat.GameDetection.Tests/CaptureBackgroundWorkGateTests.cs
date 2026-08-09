using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class CaptureBackgroundWorkGateTests
{
    [Fact]
    public void CaptureState_CancelsOldWorkAndLeavesFreshTokenAfterStop()
    {
        CaptureBackgroundWorkGate.EndCapture();
        var before = CaptureBackgroundWorkGate.CaptureCancellation;

        try
        {
            CaptureBackgroundWorkGate.BeginCapture();
            Assert.True(CaptureBackgroundWorkGate.IsCaptureActive);
            Assert.True(before.IsCancellationRequested);

            CaptureBackgroundWorkGate.EndCapture();
            Assert.False(CaptureBackgroundWorkGate.IsCaptureActive);
            Assert.False(CaptureBackgroundWorkGate.CaptureCancellation.IsCancellationRequested);
        }
        finally
        {
            CaptureBackgroundWorkGate.EndCapture();
        }
    }
}
