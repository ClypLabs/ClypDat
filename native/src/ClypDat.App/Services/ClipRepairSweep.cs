using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>
/// One-time-per-clip background pass that finds clips written with mismatched
/// H.264 parameter sets (see <see cref="ClipCorruptionRepairService"/>) and
/// repairs them in place.
///
/// Every clip is inspected at most once. The inspection is a decode of a handful
/// of frames, which is cheap, but a library is thousands of clips and the sweep
/// must never compete with recording or with the user's own playback - so it runs
/// one clip at a time, well behind the library refresh that schedules it, and
/// remembers what it has already looked at.
/// </summary>
public static class ClipRepairSweep
{
    private const string FileName = "clip-repair.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Bumped when a bug made previous results untrustworthy, so the fixed build
    // re-examines everything instead of trusting entries written by the broken
    // one. Version 2: repairs were recorded as done while every File.Replace was
    // failing across volumes, and healthy clips opening on a black frame were
    // being flagged. Version 3: a clip locked by the app's own preview reader
    // was written off as unrepairable instead of retried.
    private const int StateVersion = 3;

    private sealed record SweepState(int Version, List<string> Inspected);

    // Identity has to survive the repair rewriting the file, so the key is the
    // path plus the original length - not a hash of contents, and not the write
    // time, which the remux changes.
    private static string Key(string clipPath, long length) =>
        clipPath.ToLowerInvariant() + "|" + length.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryEntry(string clipPath, out (string Path, string Key, DateTime WrittenUtc) entry)
    {
        entry = default;
        try
        {
            var info = new FileInfo(clipPath);
            if (!info.Exists) return false;
            entry = (clipPath, Key(clipPath, info.Length), info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes staging folders a repair interrupted mid-flight.</summary>
    private static void RemoveAbandonedWorkFolders(IReadOnlyList<string> clipPaths)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clipPath in clipPaths)
        {
            var directory = Path.GetDirectoryName(clipPath);
            if (!string.IsNullOrEmpty(directory)) folders.Add(directory);
        }
        foreach (var directory in folders)
        {
            try
            {
                foreach (var stale in Directory.EnumerateDirectories(directory, ".clypdat-repair-*"))
                {
                    try { Directory.Delete(stale, recursive: true); AppLog.Info($"Clip repair: removed an abandoned staging folder ({stale})."); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// What the sweep is working on, for the per-clip overlays. <see cref="Current"/>
    /// is the clip being repaired right now; <see cref="Pending"/> is the queue
    /// behind it, in order. <see cref="Average"/> is the mean repair time
    /// measured this session, which is what the tiles' estimates come from.
    /// </summary>
    public readonly record struct Progress(string? Current, DateTime CurrentStartedUtc, IReadOnlyList<string> Pending, TimeSpan Average);

    // Detection is ffmpeg-bound, not disk-bound, and each check is well under
    // 100ms. Running a few at once turns a 400-clip library from most of a
    // minute into a few seconds, which is the difference between the user
    // seeing a corrupt clip get fixed and seeing it just sit there broken.
    private static readonly int DetectionConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>
    /// Inspects clips that have not been looked at before, repairing any that are
    /// corrupt. Safe to call on every library refresh; returns how many were fixed.
    ///
    /// Detection runs over everything first so the repair phase knows how many
    /// clips it is about to fix - which is what lets the UI show real progress
    /// rather than a bar that cannot say how far along it is.
    /// </summary>
    public static async Task<int> RunAsync(string libraryRoot, IReadOnlyList<string> clipPaths,
        Func<string, Task>? onRepaired, IProgress<Progress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || clipPaths.Count == 0) return 0;
        if (!FfmpegPathResolver.IsAvailable) return 0;
        // A second refresh landing mid-sweep must not start a competing pass.
        if (!await Gate.WaitAsync(0, token).ConfigureAwait(false)) return 0;

        try
        {
            var inspected = Load(libraryRoot);
            var repaired = 0;
            var added = 0;

            // A repair that was interrupted - the app closed, the machine slept -
            // leaves its staging folder behind holding a full copy of the clip.
            RemoveAbandonedWorkFolders(clipPaths);

            var pending = new List<(string Path, string Key, DateTime WrittenUtc)>();
            foreach (var clipPath in clipPaths)
            {
                if (TryEntry(clipPath, out var entry) && !inspected.Contains(entry.Key)) pending.Add(entry);
            }
            if (pending.Count == 0) return 0;

            // Newest first. Only builds from a narrow window produced these
            // clips, so the ones worth finding are the recent ones - checking
            // chronologically would spend a minute on years-old clips before
            // reaching anything broken.
            pending.Sort((left, right) => right.WrittenUtc.CompareTo(left.WrittenUtc));

            // Phase 1 - detection. Read-only, and it is what gates the expensive
            // part: only a clip whose decoder actually complains is ever
            // rewritten. Ordered results, concurrent execution.
            var verdicts = new bool[pending.Count];
            var next = -1;
            var workers = new Task[Math.Min(DetectionConcurrency, pending.Count)];
            for (var w = 0; w < workers.Length; w++)
            {
                workers[w] = Task.Run(async () =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next);
                        if (index >= pending.Count) return;
                        token.ThrowIfCancellationRequested();
                        verdicts[index] = await ClipCorruptionRepairService
                            .IsCorruptAsync(pending[index].Path, token).ConfigureAwait(false);
                    }
                }, token);
            }
            await Task.WhenAll(workers).ConfigureAwait(false);

            var corrupt = new List<(string Path, string Key)>();
            for (var i = 0; i < pending.Count; i++)
            {
                if (verdicts[i]) corrupt.Add((pending[i].Path, pending[i].Key));
                else { inspected.Add(pending[i].Key); added++; }
            }
            if (added > 0) Save(libraryRoot, inspected);
            if (corrupt.Count == 0) return 0;

            AppLog.Info($"Clip repair sweep: {corrupt.Count} clip(s) need repair.");

            // Phase 2 - repair, one at a time, publishing the queue so each
            // waiting clip can show its own estimate.
            var elapsedTotal = TimeSpan.Zero;
            var completedRepairs = 0;
            for (var i = 0; i < corrupt.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (clipPath, key) = corrupt[i];
                var average = completedRepairs == 0
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromTicks(elapsedTotal.Ticks / completedRepairs);
                progress?.Report(new Progress(clipPath, DateTime.UtcNow,
                    corrupt.Skip(i + 1).Select(entry => entry.Path).ToArray(), average));

                var repairClock = System.Diagnostics.Stopwatch.StartNew();
                var result = await ClipCorruptionRepairService.RepairAsync(clipPath, token).ConfigureAwait(false);
                repairClock.Stop();
                if (result.Status == ClipCorruptionRepairService.RepairStatus.Repaired)
                {
                    elapsedTotal += repairClock.Elapsed;
                    completedRepairs++;
                }
                switch (result.Status)
                {
                    case ClipCorruptionRepairService.RepairStatus.Repaired:
                        repaired++;
                        // The file changed, so its old key is meaningless and the
                        // new one has to be recorded against the new length.
                        try { inspected.Add(Key(clipPath, new FileInfo(clipPath).Length)); } catch (IOException) { }
                        added++;
                        // Save immediately: a repair is expensive and must not be
                        // repeated because the process closed before the batched
                        // write.
                        Save(libraryRoot, inspected);
                        if (onRepaired is not null) await onRepaired(clipPath).ConfigureAwait(false);
                        break;
                    case ClipCorruptionRepairService.RepairStatus.Healthy:
                    case ClipCorruptionRepairService.RepairStatus.Unrepairable:
                        // Conclusive: nothing further to try on this file, so
                        // stop looking at it every launch.
                        inspected.Add(key);
                        added++;
                        break;
                    case ClipCorruptionRepairService.RepairStatus.Skipped:
                        // Locked, missing, on a disconnected share - worth
                        // another look next refresh, so record nothing.
                        break;
                }

            }

            progress?.Report(new Progress(null, DateTime.UtcNow, Array.Empty<string>(), TimeSpan.Zero));
            if (added > 0) Save(libraryRoot, inspected);
            if (repaired > 0) AppLog.Info($"Clip repair sweep: repaired {repaired} clip(s) with mismatched encoder parameter sets.");
            return repaired;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception error)
        {
            AppLog.Error("Clip repair sweep failed.", error);
            return 0;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HashSet<string> Load(string libraryRoot)
    {
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(LibraryLayout.ClipInfoRoot(libraryRoot), FileName);
        if (!File.Exists(path)) return inspected;
        try
        {
            var state = JsonSerializer.Deserialize<SweepState>(File.ReadAllText(path));
            if (state?.Version != StateVersion) return inspected;
            if (state.Inspected is not null) inspected.UnionWith(state.Inspected.Where(k => !string.IsNullOrWhiteSpace(k)));
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip repair state read failed: {path}", error);
        }
        return inspected;
    }

    private static void Save(string libraryRoot, HashSet<string> inspected)
    {
        var path = Path.Combine(LibraryLayout.ClipInfoRoot(libraryRoot), FileName);
        var temporaryPath = path + ".tmp";
        try
        {
            LibraryLayout.EnsureClipInfoRoot(libraryRoot);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SweepState(StateVersion, inspected.ToList()), JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Error($"Clip repair state write failed: {path}", error);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* best effort */ }
        }
    }
}
