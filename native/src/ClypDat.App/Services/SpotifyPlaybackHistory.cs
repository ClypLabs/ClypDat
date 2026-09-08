using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// QPC seconds and the kernel boot identity are shared by both processes.
// Wall-clock corrections never move recorded samples or recovered windows.
internal sealed class SpotifyPlaybackHistory
{
    internal sealed record Entry(double Seconds, SpotifyNowPlaying State);
    private sealed record Journal(string Boot, Entry[] Entries, Dictionary<string, double> Pins);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, double> _pins = new();
    private readonly string _path;
    private readonly string _boot = MonotonicClock.BootId;
    public SpotifyPlaybackHistory(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataPaths.Root, "spotify-history.json");
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 16 * 1024 * 1024) return;
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path));
            if (journal is null || journal.Boot != _boot) return;
            _entries.AddRange(journal.Entries);
            foreach (var pin in journal.Pins) _pins[pin.Key] = pin.Value;
        }
        catch (Exception error) { AppLog.Error("Spotify history recovery failed.", error); }
    }
    public void Sample(object? sender, SpotifyNowPlaying state) => Add(MonotonicClock.SharedSeconds, state);
    internal void Add(double seconds, SpotifyNowPlaying state)
    {
        lock (_gate)
        {
            _entries.Add(new(seconds, state));
            var cutoff = seconds - 25 * 60;
            if (_pins.Count > 0) cutoff = Math.Min(cutoff, _pins.Values.Min());
            var first = _entries.FindLastIndex(item => item.Seconds < cutoff);
            if (first > 0) _entries.RemoveRange(0, first);
            Persist();
        }
    }
    public void Pin(string id)
    {
        lock (_gate) { _pins.TryAdd(id, MonotonicClock.SharedSeconds - 25 * 60); Persist(); }
    }
    public void Release(string? id)
    {
        if (id is null) return;
        lock (_gate) { _pins.Remove(id); Persist(); }
    }
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Journal(_boot, _entries.ToArray(), new(_pins))));
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception error) { AppLog.Error("Spotify history persistence failed.", error); }
    }
    public SpotifyTimeline Materialize(double start, double duration)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var result = new List<SpotifyTimelineSample>();
        var previous = entries.LastOrDefault(item => item.Seconds <= start);
        AddState(previous, start);
        foreach (var entry in entries.Where(item => item.Seconds > start && item.Seconds < start + duration))
        {
            Expire(previous, entry.Seconds);
            AddState(entry, entry.Seconds);
            previous = entry;
        }
        Expire(previous, start + duration);
        return new(SpotifyTimeline.CurrentVersion, result.OrderBy(item => item.OffsetSeconds).ToArray());

        void Expire(Entry? entry, double until)
        {
            if (entry is not null && entry.Seconds + 6 > start && entry.Seconds + 6 < until)
                result.Add(Unavailable(entry.Seconds + 6 - start));
        }
        void AddState(Entry? entry, double at)
        {
            if (entry is null || at - entry.Seconds >= 6 || !entry.State.IsConnected || string.IsNullOrWhiteSpace(entry.State.Track))
            { result.Add(Unavailable(at - start)); return; }
            var state = entry.State;
            var progress = state.Progress?.TotalMilliseconds;
            if (progress is not null && state.IsPlaying) progress += (at - entry.Seconds) * 1000;
            if (progress is not null && state.Duration is { } length) progress = Math.Min(progress.Value, length.TotalMilliseconds);
            result.Add(new(at - start, state.TrackId, state.Track, state.Artist, state.Album,
                state.Duration is { } d ? (int)d.TotalMilliseconds : null, progress is { } p ? (int)p : null,
                state.IsPlaying, null, true, ArtUrl: state.ArtUrl));
        }
    }
    private static SpotifyTimelineSample Unavailable(double offset) => new(offset, null, null, null, null, null, null, false, null, false);
}

internal sealed record SpotifySourceWindow(double StartSeconds, double DurationSeconds, string BootId)
{
    public static void Save(string root, string clip, DateTime start, double duration)
    {
        var path = LibraryLayout.SidecarPath(root, clip, ".source.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new SpotifySourceWindow(MonotonicClock.ToSharedSeconds(start), duration, MonotonicClock.BootId)));
    }
    public static SpotifySourceWindow? Load(string root, string clip)
    {
        try
        {
            var window = JsonSerializer.Deserialize<SpotifySourceWindow>(File.ReadAllText(LibraryLayout.SidecarPath(root, clip, ".source.json")));
            return window?.BootId == MonotonicClock.BootId ? window : null;
        }
        catch { return null; }
    }
}
