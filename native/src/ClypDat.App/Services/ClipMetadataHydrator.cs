using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// Where a card's metadata (duration, streams) stands. A stub has not been
// probed yet; a failed card was probed and ffprobe could not read it, which is
// not the same as waiting for the library sweep to get to it.
public enum ClipMetadataState { Stub, Loading, Ready, Failed }

// Why a card cannot be opened right now. Each has its own sentence, and only a
// missing or failed probe is something a click can fix.
public enum ClipOpenBlocker { None, MetadataMissing, Finalizing, SpotifyProcessing, MetadataFailed }

internal static class ClipOpenMessages
{
    public const string Loading = "Loading clip info…";
    public const string Finalizing = "Finishing this recording…";
    public const string SpotifyProcessing = "Adding Spotify overlay…";
    public const string Failed = "Couldn't read this clip's media info.";

    public static string For(ClipOpenBlocker blocker) => blocker switch
    {
        ClipOpenBlocker.Finalizing => Finalizing,
        ClipOpenBlocker.SpotifyProcessing => SpotifyProcessing,
        ClipOpenBlocker.MetadataFailed => Failed,
        _ => Loading,
    };
}

internal sealed record ClipMetadataResult(MediaFileInfo? Media, ClipOpenBlocker Blocker, string? Error)
{
    public bool IsReady => Blocker == ClipOpenBlocker.None && Media is not null;
    public static bool IsUsable(MediaFileInfo media) => media.Duration > TimeSpan.Zero && media.Tracks.Count > 0;
}

// Metadata for one clip, on demand: a click (foreground), the library sweep,
// the folder watcher and post-save processing (background). A path has at most
// one probe in flight; a later caller for the same path joins it, so a click
// on the card the sweep or a post-save is probing waits for that answer
// instead of starting a second ffprobe. The probe itself is never cancelled by
// a caller giving up, since another caller may be joined to it; a background
// caller stops waiting through its token instead.
//
// None of this looks at whether a game is running. The sweep defers itself
// while one is (HydrateLibraryClipsAsync); a clip the user asked for is
// foreground work and is probed regardless.
internal sealed class ClipMetadataHydrator
{
    public delegate Task<MediaMetadataProbe> ProbeFunction(string path, bool foreground, bool bypassCache);

    private readonly ProbeFunction _probe;
    private readonly Func<string, bool> _recordingActive;
    private readonly Func<string, bool> _spotifyProcessing;
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<ClipMetadataResult>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ClipMetadataHydrator(ProbeFunction probe, Func<string, bool>? recordingActive = null, Func<string, bool>? spotifyProcessing = null)
    {
        _probe = probe;
        _recordingActive = recordingActive ?? RecordingFileOwnership.IsActive;
        // Post-save processing probes its own clip from inside the Spotify
        // writer's scope; everyone else waits for the writer to finish.
        _spotifyProcessing = spotifyProcessing ?? (path => SpotifyProcessingPaths.IsProcessing(path) && !SpotifyProcessingPaths.IsOwnedByCaller(path));
    }

    public Task<ClipMetadataResult> EnsureAsync(string path, bool foreground, CancellationToken cancellationToken = default)
    {
        // An actively written file is not probed at all: its header is not
        // final, and a failed probe of it would read as a broken clip.
        if (_recordingActive(path)) return Task.FromResult(new ClipMetadataResult(null, ClipOpenBlocker.Finalizing, null));
        if (_spotifyProcessing(path)) return Task.FromResult(new ClipMetadataResult(null, ClipOpenBlocker.SpotifyProcessing, null));

        Task<ClipMetadataResult> task;
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(path, out task!))
            {
                task = Task.Run(() => ProbeAsync(path, foreground));
                _inFlight[path] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (_sync)
                    {
                        if (_inFlight.TryGetValue(path, out var current) && current == task) _inFlight.Remove(path);
                    }
                }, TaskScheduler.Default);
            }
        }

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task<ClipMetadataResult> ProbeAsync(string path, bool foreground)
    {
        try
        {
            var probe = await _probe(path, foreground, false).ConfigureAwait(false);
            if (ClipMetadataResult.IsUsable(probe.Media)) return new ClipMetadataResult(probe.Media, ClipOpenBlocker.None, null);
            if (_spotifyProcessing(path)) return new ClipMetadataResult(null, ClipOpenBlocker.SpotifyProcessing, null);

            // A cached answer that cannot open the clip is not trusted: read
            // the file itself before calling it unreadable.
            if (probe.FromCache)
            {
                probe = await _probe(path, foreground, true).ConfigureAwait(false);
                if (ClipMetadataResult.IsUsable(probe.Media)) return new ClipMetadataResult(probe.Media, ClipOpenBlocker.None, null);
            }

            return new ClipMetadataResult(probe.Media, ClipOpenBlocker.MetadataFailed, probe.Error ?? "ffprobe found no duration or streams.");
        }
        catch (Exception error)
        {
            // The recording can start being rewritten (a Full Session) between
            // the check above and the probe.
            if (_recordingActive(path)) return new ClipMetadataResult(null, ClipOpenBlocker.Finalizing, null);
            return new ClipMetadataResult(null, ClipOpenBlocker.MetadataFailed, error.Message);
        }
    }
}

// A card click that does not require the card to be ready. A card whose
// metadata is missing (or failed before) is probed there and then as
// foreground work - background producers park while it runs - and opens as
// soon as the probe answers, with no second click. Repeated clicks on a card
// that is already loading are ignored, and a click on another card while one
// loads wins: the earlier card does not open over it.
//
// Runs on the caller's thread: the app calls it on the UI thread, which is
// where the card updates below have to happen.
internal sealed class ClipOpenFlow
{
    private readonly ClipMetadataHydrator _hydrator;
    private string? _loadingPath;
    private int _request;

    public ClipOpenFlow(ClipMetadataHydrator hydrator) => _hydrator = hydrator;

    // The media to open, or null after telling the user why not.
    public async Task<MediaFileInfo?> ResolveAsync(ClipCardViewModel clip, Action<string> notify)
    {
        if (clip.IsOpenable)
        {
            ++_request;
            return clip.Media;
        }

        var blocker = clip.OpenBlocker;
        if (blocker is ClipOpenBlocker.Finalizing or ClipOpenBlocker.SpotifyProcessing)
        {
            notify(ClipOpenMessages.For(blocker));
            return null;
        }

        if (string.Equals(_loadingPath, clip.Path, StringComparison.OrdinalIgnoreCase)) return null;
        var request = ++_request;
        _loadingPath = clip.Path;
        notify(ClipOpenMessages.Loading);
        clip.SetMetadataState(ClipMetadataState.Loading);
        ClipMetadataResult result;
        using (EditorForegroundWork.Begin())
        {
            try
            {
                result = await _hydrator.EnsureAsync(clip.Path, foreground: true);
            }
            finally
            {
                if (string.Equals(_loadingPath, clip.Path, StringComparison.OrdinalIgnoreCase)) _loadingPath = null;
            }
        }

        clip.ApplyMetadata(result, reloadSidecars: true);
        if (result.Blocker == ClipOpenBlocker.MetadataFailed) AppLog.Info($"Clip metadata unreadable: {clip.Path}: {result.Error}");
        if (request != _request) return null; // Another card was clicked meanwhile.
        if (clip.IsOpenable) return clip.Media;
        notify(ClipOpenMessages.For(clip.OpenBlocker));
        return null;
    }
}

// A saved clip's library metadata is mandatory; Spotify's post-save work is
// optional. Whatever that job did - skipped, failed, cancelled, or never ran
// its body - the card is hydrated once it is over.
internal static class PostSaveMetadata
{
    public static async Task EnsureAfterAsync(Task postSave, Func<bool> hydrated, Func<Task> hydrate)
    {
        try { await postSave; }
        catch { /* Spotify's outcome; the metadata below does not depend on it. */ }
        if (hydrated()) return;
        try { await hydrate(); }
        catch (Exception error) { AppLog.Error("Library metadata after save failed", error); }
    }
}
