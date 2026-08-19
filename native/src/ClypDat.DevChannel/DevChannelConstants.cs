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
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAYEAwTbRg7gJ1dfCQhWNIyf5swpPVhM0+cZNiLciFJv+wOi8/3ONBtul2xQVLaKRanst8Q0/tw75lk2DfPlMmrHqXr9SJw3ZxlpOGgi2bIqC83hd5JFF9z8tQfwfe8I5Q/uRkYX1MBkupqGYYkErZdl+WF3fAwuF4QJ+xELEo4u76lb8+WmYrUIH/UAhlVGBf7IUVykCgVXNW0+BWreArJjckkYAfecqwIsC83PyEZklYxNGInf8olgFnCt0qaOwluPSJDBwJ1DTn7gmSZwOz5Uq9cVLTh61jMpBmmpBHg+errUUDJ3dboclIPzCO5NC+0pmiPZEI2qxVd5q4/5fRxnEoGlHZnPi+ueD4ZB/CmBMle6jdrAZzaRTFU8ZvJDIm7uqTLMGtrBdjfksv4ohGOrD9nIqQpRSJ9bCjp3kyqTICWoAvP3Ze7ZgZx6y4BEJ5gpsBd9i5Xiu/rBbQ8DPMwGuq9itVt9kxk4is4SDYLrUpLorFdQ6HGhyK1r1aEFrzympAgMBAAE=";

    public static string PublicKeyPem =>
        "-----BEGIN PUBLIC KEY-----\n" +
        Convert.ToBase64String(Convert.FromBase64String(PublicKeySubjectPublicKeyInfoBase64), Base64FormattingOptions.InsertLineBreaks) +
        "\n-----END PUBLIC KEY-----\n";
}
