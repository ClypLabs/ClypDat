namespace ClypDat.Core.Settings;

public static class AppDataPaths
{
    private static string _productFolder = "ClypDat";

    public static string ProductFolderName => _productFolder;
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _productFolder);

    public static void ConfigureProductFolder(string productFolder)
    {
        if (string.IsNullOrWhiteSpace(productFolder) || productFolder.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("Invalid product folder.", nameof(productFolder));
        _productFolder = productFolder;
    }
}
