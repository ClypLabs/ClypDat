using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void GetModifierVariants_NoModifiers_AllowsExtraControlAndShift()
    {
        Assert.Equal([0x0u, 0x2u, 0x4u, 0x6u], GlobalHotkeyService.GetModifierVariants(0));
    }

    [Fact]
    public void GetModifierVariants_ControlConfigured_AllowsExtraShift()
    {
        Assert.Equal([0x2u, 0x6u], GlobalHotkeyService.GetModifierVariants(0x2));
    }

    [Fact]
    public void GetModifierVariants_ControlAndShiftConfigured_DoesNotDuplicateRegistration()
    {
        Assert.Equal([0x6u], GlobalHotkeyService.GetModifierVariants(0x6));
    }
}
