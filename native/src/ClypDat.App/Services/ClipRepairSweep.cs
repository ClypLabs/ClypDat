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
    // being flagged.
    private const int StateVersion = 2;

    private sealed record SweepState(int Version, List<string> Inspected);

    // Identity has to survive the repair rewriting the file, so the key is the
    // path plus the original length - not a hash of contents, and not the write
    // time, which the remux changes.
    private static string Key(string clipPath, long length) =>
        clipPath.ToLowerInvariant() + "|" + length.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Inspects clips that have not been looked at before, repairing any that are
    /// corrupt. Safe to call on every library refresh; returns how many were fixed.
    /// </summary>
    public static async Task<int> RunAsync(string libraryRoot, IReadOnlyList<string> clipPaths, CancellationToken token)
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

            foreach (var clipPath in clipPaths)
            {
                token.ThrowIfCancellationRequested();
                long length;
                try
                {
                    var info = new FileInfo(clipPath);
                    if (!info.Exists) continue;
                    length = info.Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var key = Key(clipPath, length);
                if (inspected.Contains(key)) continue;

                // Detection is read-only and cheap, so it is what gates the
                // expensive part: only a clip whose decoder actually complains
                // is ever rewritten.
                if (!await ClipCorruptionRepairService.IsCorruptAsync(clipPath, token).ConfigureAwait(false))
                {
                    inspected.Add(key);
                    added++;
                    continue;
                }

                var result = await ClipCorruptionRepairService.RepairAsync(clipPath, token).ConfigureAwait(false);
                switch (result.Status)
                {
                    case ClipCorruptionRepairService.RepairStatus.Repaired:
                        repaired++;
                        // The file changed, so its old key is meaningless and the
                        // new one has to be recorded against the new length.
                        try { inspected.Add(Key(clipPath, new FileInfo(clipPath).Length)); } catch (IOException) { }
                        added++;
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

                if (added % 25 == 0 && added > 0) Save(libraryRoot, inspected);

                // Yield the disk between clips. This is maintenance; nothing is
                // waiting on it.
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }

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
