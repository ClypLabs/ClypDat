using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class UpdatePresentationPolicyTests
{
    [Fact]
    public void AutomaticChecks_UpdateBadgeWithoutDialog()
    {
        Assert.Equal(UpdateCheckPresentation.BadgeOnly, UpdatePresentationPolicy.ForAutomaticCheck());
    }

    [Fact]
    public void UserRequestedChecks_MayOpenDialog()
    {
        Assert.Equal(UpdateCheckPresentation.Dialog, UpdatePresentationPolicy.ForUserAction());
    }
}
