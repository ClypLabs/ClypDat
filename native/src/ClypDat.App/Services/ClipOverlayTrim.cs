using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>Produces trimmed input histories before a clip is replaced. The
/// caller owns installation, so cancellation or encoding failure cannot alter
/// an existing overlay.</summary>
internal static class ClipOverlayTrim
{
    public static StagedInput? StageInput(string libraryRoot, ClipOverlayManifest? manifest,
        double trimStart, double trimEnd, double speed, CancellationToken cancellationToken)
    {
        var layer = manifest?.Peripherals;
        var target = ClipOverlayManifest.ResolveAssetPath(libraryRoot, layer?.InputIndexPath ?? layer?.AssetPath);
        if (target is null || !File.Exists(target)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        InputCaptureIndex index;
        try
        {
            index = JsonSerializer.Deserialize<InputCaptureIndex>(File.ReadAllText(target))
                ?? throw new InvalidDataException("Input overlay index is empty.");
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Input overlay index cannot be trimmed: {target}", error);
        }

        if (speed <= 0 || !double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        var held = HeldAt(index, trimStart);
        double Rebase(double seconds) => (seconds - trimStart) / speed;
        var transitions = index.Transitions
            .Where(item => item.Seconds >= trimStart && item.Seconds <= trimEnd)
            .Select(item => item with { Seconds = Rebase(item.Seconds) }).ToArray();
        var checkpoints = index.Checkpoints
            .Where(item => item.Seconds > trimStart && item.Seconds <= trimEnd)
            .Select(item => item with { Seconds = Rebase(item.Seconds) }).ToList();
        checkpoints.Insert(0, new InputCheckpoint(0, held));
        var rebased = new InputCaptureIndex(index.Version, index.MissingHistory, transitions, checkpoints);
        var temporary = target + $".trim-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(rebased));
        return new StagedInput(target, temporary);
    }

    private static IReadOnlyList<InputPhysicalKey> HeldAt(InputCaptureIndex index, double seconds)
    {
        var held = new HashSet<InputPhysicalKey>();
        var checkpoint = index.Checkpoints.LastOrDefault(item => item.Seconds <= seconds);
        var from = checkpoint?.Seconds ?? double.NegativeInfinity;
        if (checkpoint is not null) foreach (var key in checkpoint.Down) held.Add(key);
        foreach (var item in index.Transitions)
        {
            if (item.Seconds <= from) continue;
            if (item.Seconds > seconds) break;
            if (item.Down) held.Add(item.Key); else held.Remove(item.Key);
        }
        return held.ToArray();
    }
}

internal sealed record StagedInput(string TargetPath, string TemporaryPath) : IDisposable
{
    public void Install()
    {
        File.Move(TemporaryPath, TargetPath, true);
    }
    public void Dispose()
    {
        try { if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath); }
        catch (Exception) { }
    }
}
