using System.Text;
using System.Text.Json;
using ClypDat.App.Services;
using Xunit;
using Pending = ClypDat.App.Services.ClipStatsReporter.Pending;

namespace ClypDat.App.Tests;

public sealed class ClipStatsAttributionTests
{
    private static string Token(string subject) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject, exp = 2_000_000_000, jti = Guid.NewGuid().ToString("N") })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    [Fact]
    public void AccountKeyIsStableAcrossRenewalsAndHidesTheId()
    {
        var first = ClipStatsReporter.AccountKey(Token("user-1"));
        Assert.NotNull(first);
        Assert.Equal(first, ClipStatsReporter.AccountKey(Token("user-1")));
        Assert.NotEqual(first, ClipStatsReporter.AccountKey(Token("user-2")));
        Assert.DoesNotContain("user-1", first);
        Assert.Null(ClipStatsReporter.AccountKey("account-security-test-token"));
        Assert.Null(ClipStatsReporter.AccountKey("!!!.x"));
        Assert.Null(ClipStatsReporter.AccountKey(Token("")));
    }

    [Fact]
    public void SavesStayWithTheAccountSignedInWhenTheyWereMade()
    {
        var pending = new Pending();
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 30, account: null);
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 45, "a");
        ClipStatsReporter.Add(pending, ClipStatKind.FullSession, 600, "a");

        Assert.Equal((1, 30L), (pending.Clip, pending.ClipSeconds));
        Assert.Equal("a", pending.SignedInAccount);
        Assert.Equal((1, 45L, 1, 600L), (pending.SignedIn!.Clip, pending.SignedIn.ClipSeconds, pending.SignedIn.FullSession, pending.SignedIn.FullSessionSeconds));
    }

    [Fact]
    public void SigningOutOrSwitchingSendsWaitingSavesWithoutAnAccount()
    {
        var pending = new Pending();
        ClipStatsReporter.Add(pending, ClipStatKind.AutoClip, 20, "a");
        ClipStatsReporter.ReleaseSignedIn(pending, "a");
        Assert.NotNull(pending.SignedIn);

        ClipStatsReporter.ReleaseSignedIn(pending, account: null);
        Assert.Null(pending.SignedIn);
        Assert.Null(pending.SignedInAccount);
        Assert.Equal((1, 20L), (pending.AutoClip, pending.AutoClipSeconds));

        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 10, "a");
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 15, "b");
        Assert.Equal("b", pending.SignedInAccount);
        Assert.Equal((1, 15L), (pending.SignedIn!.Clip, pending.SignedIn.ClipSeconds));
        Assert.Equal((1, 10L), (pending.Clip, pending.ClipSeconds));
    }

    [Fact]
    public void ASentBatchComesOffWhereverItNowSits()
    {
        var pending = new Pending();
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 30, account: null);
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 40, "a");
        ClipStatsReporter.Add(pending, ClipStatKind.Clip, 50, "a");
        var batch = new Pending { Clip = 2, ClipSeconds = 90 };

        var sentSignedIn = Clone(pending);
        ClipStatsReporter.Sent(sentSignedIn, batch, "a");
        Assert.Null(sentSignedIn.SignedIn);
        Assert.Null(sentSignedIn.SignedInAccount);
        Assert.Equal((1, 30L), (sentSignedIn.Clip, sentSignedIn.ClipSeconds));

        // Another process released them while the request was out.
        var released = Clone(pending);
        ClipStatsReporter.ReleaseSignedIn(released, account: null);
        ClipStatsReporter.Sent(released, batch, "a");
        Assert.Equal((1, 30L), (released.Clip, released.ClipSeconds));

        var sentAnonymous = Clone(pending);
        ClipStatsReporter.Sent(sentAnonymous, new Pending { Clip = 1, ClipSeconds = 30 }, account: null);
        Assert.True(sentAnonymous.IsEmpty);
        Assert.Equal(2, sentAnonymous.SignedIn!.Clip);
    }

    [Fact]
    public void PendingFilesFromBeforeAccountsReadAsSignedOutSaves()
    {
        var old = JsonSerializer.Deserialize<Pending>("""{"Clip":3,"AutoClip":0,"FullSession":1,"ClipSeconds":90,"IsEmpty":false}""")!;
        Assert.Equal(3, old.Clip);
        Assert.Null(old.SignedIn);
        Assert.Null(old.SignedInAccount);
        Assert.DoesNotContain("IsEmpty", JsonSerializer.Serialize(old));
    }

    private static Pending Clone(Pending pending) => JsonSerializer.Deserialize<Pending>(JsonSerializer.Serialize(pending))!;
}
