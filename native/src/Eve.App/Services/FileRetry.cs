namespace Eve.App.Services;

// Retries a file mutation a few times with a short delay before giving up -
// network shares routinely throw a transient IOException/UnauthorizedAccessException
// (a brief disconnect blip, AV scan, SMB lock) that succeeds a moment later on
// its own. Mirrors the retry pattern already used for buffer-segment files in
// AudioCapturePipeline.WaitForFileAsync/WindowsReplayBuffer.WaitForFileAsync,
// applied here to library-folder mutations (rename/delete) where a single
// failed attempt previously meant "the operation just silently didn't happen."
public static class FileRetry
{
    private const int Attempts = 5;
    private const int DelayMs = 200;

    public static async Task RunAsync(Action action, string description)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    AppLog.Error($"{description} failed after {Attempts} attempts.", error);
                    throw;
                }

                AppLog.Info($"{description} failed (attempt {attempt}/{Attempts}), retrying: {error.Message}");
                await Task.Delay(DelayMs);
            }
        }
    }
}
