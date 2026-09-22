namespace ClypDat.App.Services;

/// <summary>
/// Trusted keys for the Notice Board feed (www.clypdat.xyz/api/notices).
///
/// Deliberately not the release keys. The notice private key lives on the website
/// (Vercel env NOTICE_SIGNING_KEY) so notices can be published without a build;
/// keeping it separate means that host can at worst publish a notice - never sign an
/// installer - and a release key can never sign a notice either.
///
/// A list for the same reason as ReleaseSigning.PinnedPublicKeys: to rotate, ship a
/// build trusting both the old and new key, move the site to the new one, then drop
/// the old one in a later build.
/// </summary>
internal static class NoticeSigning
{
    public static readonly IReadOnlyList<PinnedReleaseKey> PinnedPublicKeys = new PinnedReleaseKey[]
    {
        new PinnedReleaseKey("notice-2026-09", "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAolG8qdbkQcto8lVflxtPUNCCAm5lYCPTPyA2RLNR9EshETwwdTQuammvvNTHdeEGtLmbVB+Zn2fZOpUNhXYOxhgknsBs+rsPwSv1lpcr5L1mWqO3hnkWyQMtSHI4FemJYcajL+UPV2tNo+kaJaqvdafU8vggTBUwI/CW94u518hq8/8El6WvOh5kEnIESyC3WatJw99xVtkfrNEysazpLS1ZyC3VoBMvnBYQjtXzoWCKLY/qnDKKeeud8D+H/CCE0o4NAzYaMRxGgVKaR/EgLPrSHc+Pe7x4qT9VQYsCog/ONT7oRE/sLQK2Zrub7yATRW6nU9QrcowejfjTmUU0ywZJozi5s0dr6vZ0i7REQhy/dNGRLw0Bn8lwVzsjZf28w6IFbYjm65zAyGf08Y7CFA/vRtlCgVcpw6f0/bYM5hCmxbblFgqbKByBYfIQ/BkkAlCJsRB1uJ5QOz2cEOqwGaiz0lg6I8490p3A1EcTzAIMYNCb6fPOAH+vT13b/QmFAgMBAAE="),
    };
}
