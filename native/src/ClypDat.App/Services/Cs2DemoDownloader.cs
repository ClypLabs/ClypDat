using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClypDat.Core.Settings;
using ICSharpCode.SharpZipLib.BZip2;

namespace ClypDat.App.Services;

internal enum Cs2DemoDownloadOutcome { Ready, Expired, Rejected, TooLarge }

internal sealed class Cs2DemoDownloadResult(Cs2DemoDownloadOutcome outcome, string? demoPath = null)
{
    public Cs2DemoDownloadOutcome Outcome { get; } = outcome;
    public string? DemoPath { get; } = demoPath;
}

// Fetches a match replay from Valve's replay servers and unpacks it next to
// itself. The URL comes from clypdat.xyz but is checked here anyway: only a
// replayNNN.valve.net .dem.bz2 is ever fetched, over the plain HTTP those
// servers speak, and both the download and what it unpacks to are capped.
// Files go in a private folder under AppData with random names, and are
// deleted by the caller once parsed - or at the next start if the app died.
internal static partial class Cs2DemoDownloader
{
    public const long MaximumCompressedBytes = 400L * 1024 * 1024;
    public const long MaximumDemoBytes = 1536L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
    {
        // Per request; the body is read under its own idle timeout below.
        Timeout = TimeSpan.FromMinutes(30)
    };

    [GeneratedRegex(@"^http://replay\d{1,4}\.valve\.net/730/\d{1,21}_\d{1,20}\.dem\.bz2$", RegexOptions.CultureInvariant)]
    private static partial Regex DemoUrlPattern();

    public static string TempFolder => Path.Combine(AppDataPaths.Root, "cs2-demos");

    public static bool IsAllowedUrl(string? url) => url is not null && url.Length < 200 && DemoUrlPattern().IsMatch(url);

    /// <summary>Removes whatever an earlier run left behind.</summary>
    public static void CleanStale()
    {
        try
        {
            if (!Directory.Exists(TempFolder)) return;
            foreach (var file in Directory.EnumerateFiles(TempFolder))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) { AppLog.Info($"Match stats: demo cleanup skipped ({error.GetType().Name})."); }
    }

    /// <summary>Downloads the compressed replay. Ready carries the .dem.bz2 path.</summary>
    public static async Task<Cs2DemoDownloadResult> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsAllowedUrl(url)) return new(Cs2DemoDownloadOutcome.Rejected);
        Directory.CreateDirectory(TempFolder);
        var name = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var compressedPath = Path.Combine(TempFolder, name + ".dem.bz2");
        var ready = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // Valve deletes replays after a few weeks.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
                return new(Cs2DemoDownloadOutcome.Expired);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumCompressedBytes) return new(Cs2DemoDownloadOutcome.TooLarge);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var file = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                if (!await CopyCappedAsync(body, file, MaximumCompressedBytes, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
                    return new(Cs2DemoDownloadOutcome.TooLarge);
            }
            ready = true;
            return new(Cs2DemoDownloadOutcome.Ready, compressedPath);
        }
        finally
        {
            if (!ready) TryDelete(compressedPath);
        }
    }

    /// <summary>
    /// Unpacks a downloaded .dem.bz2 beside itself and deletes it. Synchronous
    /// on purpose: BZip2 is CPU-bound and the caller runs it on a low-priority
    /// thread. Never more than a buffer of either file is held in memory.
    /// </summary>
    public static Cs2DemoDownloadResult Unpack(string compressedPath, CancellationToken cancellationToken)
    {
        var demoPath = Path.ChangeExtension(compressedPath, null);
        var ready = false;
        try
        {
            using var compressed = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var unpacked = new BZip2InputStream(compressed) { IsStreamOwner = false };
            using var output = new FileStream(demoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            if (!CopyCapped(unpacked, output, MaximumDemoBytes, cancellationToken)) return new(Cs2DemoDownloadOutcome.TooLarge);
            ready = true;
            return new(Cs2DemoDownloadOutcome.Ready, demoPath);
        }
        finally
        {
            TryDelete(compressedPath);
            if (!ready) TryDelete(demoPath);
        }
    }

    internal static async Task<bool> CopyCappedAsync(Stream source, Stream destination, long cap, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            int read;
            try { read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The replay download stalled.");
            }
            if (read == 0) return true;
            total += read;
            if (total > cap) return false;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool CopyCapped(Stream source, Stream destination, long cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > cap) return false;
            destination.Write(buffer, 0, read);
        }
        return true;
    }

    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
