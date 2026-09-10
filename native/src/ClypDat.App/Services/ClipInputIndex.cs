using System.Text.Json;

namespace ClypDat.App.Services;

// The recorded shape, moved out of RawInputRecorder so the reader and the
// writer can share it. Property names are load-bearing: sidecars already on
// disk were written with default JsonSerializer options, so these must stay
// exactly as they were when nested.

/// <summary>
/// v2 intentionally uses clip-relative seconds. UTC values are not stable after
/// a clip is moved, copied, trimmed, or opened on another machine.
/// </summary>
internal sealed record InputCaptureIndex(int Version, string? MissingHistory,
    IReadOnlyList<InputTransition> Transitions, IReadOnlyList<InputCheckpoint> Checkpoints);

internal sealed record InputTransition(double Seconds, InputPhysicalKey Key, bool Down, string Kind);

internal sealed record InputCheckpoint(double Seconds, IReadOnlyList<InputPhysicalKey> Down);

internal sealed record InputPhysicalKey(ushort ScanCode, bool E0, bool E1, string? MouseButton = null);

/// <summary>
/// Reads a clip's recorded input and answers what was held at a given moment.
/// The format was built for this: every transition is an edge, and a checkpoint
/// carrying the whole held set is written at least every two seconds, so any
/// seek target only needs the nearest checkpoint plus the edges after it.
/// </summary>
internal static class ClipInputIndex
{
    public static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static InputCaptureIndex? Load(string libraryRoot, ClipOverlayLayer? layer)
    {
        var relative = layer?.InputIndexPath ?? layer?.AssetPath;
        var path = ClipOverlayManifest.ResolveAssetPath(libraryRoot, relative);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var index = JsonSerializer.Deserialize<InputCaptureIndex>(File.ReadAllText(path));
            // A history that recorded nothing is not the same as an idle
            // keyboard, and the editor should be able to tell them apart.
            return index is null || index.MissingHistory is not null ? null : index;
        }
        catch (Exception error)
        {
            AppLog.Debug($"Overlay input index could not be read ({error.Message}).");
            return null;
        }
    }

    /// <summary>The key codes held at <paramref name="seconds"/>, ready to match
    /// against what the board draws.</summary>
    public static IReadOnlySet<string> PressedAt(InputCaptureIndex? index, double seconds)
    {
        if (index is null || !double.IsFinite(seconds)) return Nothing;
        var held = new HashSet<InputPhysicalKey>();
        var from = double.NegativeInfinity;
        // Checkpoints are written in order, so the last one at or before the
        // requested time is the cheapest complete starting state.
        for (var i = index.Checkpoints.Count - 1; i >= 0; i--)
        {
            if (index.Checkpoints[i].Seconds > seconds) continue;
            foreach (var key in index.Checkpoints[i].Down) held.Add(key);
            from = index.Checkpoints[i].Seconds;
            break;
        }
        foreach (var transition in index.Transitions)
        {
            if (transition.Seconds <= from) continue;
            if (transition.Seconds > seconds) break;
            if (transition.Down) held.Add(transition.Key); else held.Remove(transition.Key);
        }
        if (held.Count == 0) return Nothing;
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in held)
        {
            var code = key.MouseButton ?? InputKeyMap.Code(key.ScanCode, key.E0);
            if (code is not null) codes.Add(code);
        }
        return codes;
    }
}
