namespace ClypDat.App.Services;

// A locked sidecar survives process termination. Its exclusive handle, rather
// than its age or a reusable PID, determines whether a writer still owns it.
internal sealed class RecordingFileOwnership : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _marker;
    private RecordingFileOwnership(string path)
    {
        _marker = path + ".recording";
        _lease = new FileStream(_marker, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
    }
    public static RecordingFileOwnership Acquire(string path) => new(path);
    public static bool IsActive(string path)
    {
        var marker = path + ".recording";
        if (!File.Exists(marker)) return false;
        try
        {
            // DeleteOnClose keeps reclamation atomic: another scanner cannot
            // mistake a still-open reclamation handle for an abandoned marker.
            using var stale = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            File.WriteAllText(FullSessionRecovery.Marker(path), "Interrupted Full Session");
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    public static void ThrowIfActive(string path)
    {
        if (IsActive(path)) throw new IOException("This recording is still being written.");
    }
    public void Dispose()
    {
        // Delete while the exclusive read/write lease still exists. Releasing
        // first would let a scanner misclassify a normal close as a crash.
        try { File.Delete(_marker); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { _lease.Dispose(); }
    }
}
