using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public static class DevChannelMode
{
#if CLYPDAT_DEV
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif

    public static string ProductFolder => Enabled ? "ClypDat-Dev" : "ClypDat";

    public static void ConfigureDataRoot()
    {
        if (Enabled) AppDataPaths.ConfigureProductFolder(ProductFolder);
    }
}
