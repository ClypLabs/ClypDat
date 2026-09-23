namespace ClypDat.App.Services;

internal static class FileCleanup
{
    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Cleanup is best effort. */ }
    }
}
