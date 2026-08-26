namespace ClypDat.DevChannel;

public static class DevChannelConstants
{
    public const string ChannelName = "Dev";
    public const string ReleaseTag = "dev-channel";
    public const string ManifestAssetName = "ClypDat-Dev.manifest.json";
    public const string SignatureAssetName = "ClypDat-Dev.manifest.sig";
    public const string ArchiveAssetName = "ClypDat-Dev.zip";
    public const long MaximumArchiveBytes = 1_073_741_824;
    public const int MaximumArchiveEntries = 100_000;
    public const string PublicKeySubjectPublicKeyInfoBase64 =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA3pdnOojXKTMkBLORjybPbvL/BidFlMOzR0ttD3nVsxCQ4Pez9kayjxC96wXWzBEOs269gBjA+2Gq6tHpz6sbCLbLP4YN0VUxbb1J8ZJAiOszscJaxjKXIDCuJcix5x/gZ/ADCks6/FCGjJ3KJBWd702RaSMSvcOWyhblPi43zeBX8V5i0Qgz7gLwd2EJQCE23HxB6r22Z8Px9KQZss6TIr99SIVK77dsBcKFmGiaWBab1soiEC0ZeoH94uDXMbgMgBWI0fZunMyqT9K7tcmM07+rHv+OLrLP425kizdxD89JtDpy9MwSwcFfE57Ho++du8Oz0/TinKb6Jv/vJFT86mIWWFuxDLdsCWjYrgv/kfvFrAazC9nwI6zPiDBup8FXdjMRMywIoujpLk7W2uU9rQXBHZBatRYAK55atlJ8irM+mfOZy7dSqaIEF8+fivLSzB7JHSy/YFRmXsmi/PnL2qUCIslXapyBo8tvbWaE0WvWJ4QY8HL7xBx+lGW0JqPvAgMBAAE=";

    public static string PublicKeyPem =>
        "-----BEGIN PUBLIC KEY-----\n" +
        Convert.ToBase64String(Convert.FromBase64String(PublicKeySubjectPublicKeyInfoBase64), Base64FormattingOptions.InsertLineBreaks) +
        "\n-----END PUBLIC KEY-----\n";
}
