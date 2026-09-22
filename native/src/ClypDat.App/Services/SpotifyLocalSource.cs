using System.Security.Cryptography;
using ClypDat.Core.Settings;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClypDat.App.Services;

/// <summary>
/// Reads what the Spotify app on this PC is playing from Windows' media
/// controls - the same source the volume flyout and lock screen show - so the
/// song works with no Spotify sign-in at all.
///
/// Spotify's Web API is closed to most users: since February 2026 an app in
/// Development Mode takes five allow-listed accounts, and extended quota is
/// for organisations only. Anyone else could press Connect, get through
/// Spotify's login, and then have every request refused. This needs no account,
/// makes no network request, and so has no cap and no rate limit. What it
/// cannot see is Spotify playing somewhere else (a phone, the web player);
/// signing in with Spotify still covers that for the accounts that can.
/// </summary>
internal sealed class SpotifyLocalSource : IDisposable
{
    // Events drive the reads; this only catches whatever an app forgot to
    // announce. A read is local and costs a few WinRT calls.
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(2);
    // Coalesces the burst of property/playback/timeline events one track
    // change raises into a single read.
    private static readonly TimeSpan EventSettle = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artKey;
    private string? _artPath;

    /// <summary>What the Spotify app is showing, or null when it is not running (or has no media session).</summary>
    public SpotifyNowPlaying? Current { get; private set; }
    public bool IsRunning => _cts is not null;

    /// <summary>Raised after every read, on a thread-pool thread.</summary>
    public event EventHandler<SpotifyNowPlaying?>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            AttachSession(null);
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
            Current = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_manager is null)
                {
                    // Missing on Windows N editions without the Media Feature
                    // Pack; retried, since that can be installed while we run.
                    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        _manager = manager;
                        _manager.SessionsChanged += OnSessionsChanged;
                    }
                }
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                AppLog.Error("Spotify (this PC): reading media controls failed.", error);
                Publish(null, cancellationToken);
                try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                await _wake.WaitAsync(SafetyPoll, cancellationToken).ConfigureAwait(false);
                await Task.Delay(EventSettle, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var manager = _manager;
        if (manager is null) return;
        var session = PickSession(manager.GetSessions());
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return;
            AttachSession(session);
        }
        if (session is null)
        {
            Publish(null, cancellationToken);
            return;
        }

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var now = DateTimeOffset.UtcNow;

        var track = Clean(properties?.Title);
        var artist = Clean(properties?.Artist);
        var album = Clean(properties?.AlbumTitle);
        var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        // Free accounts' adverts arrive as a track with no artist.
        if (IsAdvert(track, artist)) track = artist = album = null;

        TimeSpan? duration = null;
        TimeSpan? progress = null;
        if (track is not null && timeline is not null)
        {
            var length = timeline.EndTime - timeline.StartTime;
            if (length > TimeSpan.Zero)
            {
                duration = length;
                var position = timeline.Position - timeline.StartTime;
                // Position is as of LastUpdatedTime. An app that reports its
                // position only on seeks would otherwise read as stuck there.
                if (isPlaying && timeline.LastUpdatedTime.Year > 2000 && timeline.LastUpdatedTime < now)
                    position += now - timeline.LastUpdatedTime;
                progress = position < TimeSpan.Zero ? TimeSpan.Zero : position > length ? length : position;
            }
        }

        string? artPath = null;
        if (track is not null)
        {
            var key = $"{track}\n{artist}\n{album}";
            if (!string.Equals(key, _artKey, StringComparison.Ordinal))
            {
                // Spotify publishes the title a moment before the cover, so a
                // read that finds none tries again on the next one.
                _artPath = await ReadThumbnailAsync(properties?.Thumbnail, cancellationToken).ConfigureAwait(false);
                _artKey = _artPath is null ? null : key;
            }
            artPath = _artPath;
        }

        Publish(new SpotifyNowPlaying(true, null, track, artist, album, duration, progress, isPlaying && track is not null,
            now, null, LocalArtPath: artPath), cancellationToken);
    }

    private void Publish(SpotifyNowPlaying? snapshot, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    // Spotify.exe registers as "Spotify.exe"; the Microsoft Store build as
    // "SpotifyAB.SpotifyMusic_...!Spotify". A playing session wins if there
    // are ever two.
    internal static GlobalSystemMediaTransportControlsSession? PickSession(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        GlobalSystemMediaTransportControlsSession? found = null;
        foreach (var session in sessions)
        {
            if (!IsSpotifyApp(session.SourceAppUserModelId)) continue;
            if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return session;
            found ??= session;
        }
        return found;
    }

    internal static bool IsSpotifyApp(string? appUserModelId) =>
        !string.IsNullOrWhiteSpace(appUserModelId) && appUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAdvert(string? track, string? artist) =>
        track is not null && artist is null &&
        (track.Equals("Advertisement", StringComparison.OrdinalIgnoreCase) || track.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session)) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _session = session;
        _artKey = null;
        _artPath = null;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Wake();
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Wake();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Wake();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Wake();

    /// <summary>Read again now rather than at the next safety poll.</summary>
    public void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<string?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (stream.Size is 0 or > SpotifyCoverArtStore.MaximumBytes) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return SpotifyLocalArt.Save(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // The song still shows; only its cover is missing.
            AppLog.Error("Spotify (this PC): reading the cover failed.", error);
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}

/// <summary>
/// Covers read from the media controls, kept in app data by content hash until
/// a saved clip moves the ones it used into the library's archive
/// (<see cref="SpotifyCoverArtStore.ImportLocalArtAsync"/>).
/// </summary>
internal static class SpotifyLocalArt
{
    // A few hours of listening. Older covers are pruned; a clip saved in that
    // window has already moved its covers into the library.
    private const int Capacity = 200;
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(AppDataPaths.Root, "spotify-local-art");

    public static string? Save(byte[] bytes)
    {
        if (bytes.Length is 0 or > SpotifyCoverArtStore.MaximumBytes) return null;
        var path = Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".jpg");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return path;
                }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                Prune();
                return path;
            }
            catch (Exception error)
            {
                AppLog.Error("Spotify (this PC): saving the cover failed.", error);
                return null;
            }
        }
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.jpg");
        if (files.Length <= Capacity) return;
        foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc).Skip(Capacity))
        {
            try { file.Delete(); } catch { }
        }
    }

    /// <summary>
    /// Whether a path is one of these cached covers. Paths arrive back out of
    /// the history journal and clip sidecars, which a shared library makes
    /// untrusted - so only a flat &lt;sha256&gt;.jpg in this folder qualifies.
    /// </summary>
    public static bool IsLocalArtPath(string? path) => IsLocalArtPath(Folder, path);

    internal static bool IsLocalArtPath(string folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.Split('\\', '/').Any(segment => segment == "..")) return false;
            var full = Path.GetFullPath(path);
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)) return false;
            var name = Path.GetFileNameWithoutExtension(full);
            return string.Equals(Path.GetExtension(full), ".jpg", StringComparison.OrdinalIgnoreCase) &&
                name.Length == 64 && name.All(Uri.IsHexDigit);
        }
        catch { return false; }
    }
}
