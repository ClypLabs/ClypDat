using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class NativeRecordingPublication
{
    internal sealed record Mapping(long SourceStartUs, long DurationUs, long OutputStartUs);
    private sealed record State(long AtUs, OverlayCaptureSettings Settings);
    internal static void Publish(ReplayBufferConfig configuration, string output, string game, string title,
        JsonElement result, long durationUs, long requestedStartUs)
    {
        var root = configuration.LibraryFolder;
        var overlayMap = Mappings(result, "overlayMappings");
        var audioMap = Mappings(result, "audioMappings");
        if (durationUs <= 0 || overlayMap.Count == 0 || audioMap.Count == 0)
            throw new InvalidDataException("Native save returned no valid media timeline.");
        var states = result.GetProperty("settings").EnumerateArray()
            .Where(item => item.GetProperty("value").ValueKind != JsonValueKind.Null)
            .Select(item => new State(item.GetProperty("atUs").GetInt64(), item.GetProperty("value").Deserialize<OverlayCaptureSettings>()!))
            .Where(item => item.Settings is not null).OrderBy(item => item.AtUs).ToArray();
        var input = result.GetProperty("input").Deserialize<InputCaptureIndex>()
            ?? throw new InvalidDataException("Native save returned no input snapshot.");
        var inputStart = result.TryGetProperty("inputStartUs", out var inputStartValue) ? inputStartValue.GetInt64() : requestedStartUs;
        input = RebaseInput(input, inputStart, overlayMap);
        var keyboardSettings = states.LastOrDefault(item => !string.Equals(item.Settings.KeyboardLayout, "None", StringComparison.OrdinalIgnoreCase))?.Settings;
        var cameraSettings = states.LastOrDefault(item => item.Settings.Camera is not null)?.Settings;
        var burned = states.FirstOrDefault()?.Settings is { } first && OverlayRecordingMode.IsBurned(first.RecordingMode);
        string? inputPath = null;
        if (keyboardSettings is not null && !burned && input.MissingHistory is null)
        {
            var path = LibraryLayout.SidecarPath(root, output, ".input.json"); WriteJson(root, path, input);
            inputPath = Path.GetRelativePath(root, path);
        }
        var cameraAssets = new List<ClipOverlayAsset>();
        if (cameraSettings is not null && !burned)
        {
            var destination = LibraryLayout.SidecarPath(root, output, ".camera");
            foreach (var segment in result.GetProperty("camera").EnumerateArray())
            {
                if (!segment.GetProperty("completed").GetBoolean()) continue;
                var start = segment.GetProperty("startUs").GetInt64(); var end = segment.GetProperty("endUs").GetInt64();
                var path = segment.GetProperty("path").GetString() ?? "";
                var intersections = overlayMap.Select(map => (Map: map, Start: Math.Max(start, map.SourceStartUs), End: Math.Min(end, map.SourceStartUs + map.DurationUs)))
                    .Where(part => part.End > part.Start).ToArray();
                if (intersections.Length == 0 || !File.Exists(path)) continue;
                RequireOwnedPath(root, destination);
                Directory.CreateDirectory(destination);
                var copied = Path.Combine(destination, Path.GetFileName(path)); RequireOwnedPath(root, copied); File.Copy(path, copied, false);
                foreach (var part in intersections)
                    cameraAssets.Add(new(Path.GetRelativePath(root, copied), (part.Map.OutputStartUs + part.Start - part.Map.SourceStartUs) / 1_000_000d,
                        (part.Map.OutputStartUs + part.End - part.Map.SourceStartUs) / 1_000_000d, (part.Start - start) / 1_000_000d));
            }
        }
        cameraAssets.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        var cameraAvailable = burned ? Flag(result, "burnedCamera") : cameraAssets.Count > 0;
        var keyboardAvailable = burned ? Flag(result, "burnedKeyboard") : inputPath is not null;
        ClipOverlayLayer? Camera(OverlayCaptureSettings? settings) => settings?.Camera is not { } camera ? null :
            new(camera.FriendlyName, cameraAvailable, InitialTransform: settings.CameraTransform.ToPresentationTransform(),
                Error: cameraAvailable ? null : "Camera did not deliver saved footage in this interval.", Flattened: burned,
                Assets: burned || cameraAssets.Count == 0 ? null : cameraAssets);
        ClipOverlayLayer? Keyboard(OverlayCaptureSettings? settings) => settings is null || string.Equals(settings.KeyboardLayout, "None", StringComparison.OrdinalIgnoreCase) ? null :
            new(settings.KeyboardLayout, keyboardAvailable, AssetPath: inputPath, InitialTransform: settings.KeyboardTransform.ToPresentationTransform(),
                Error: keyboardAvailable ? null : input.MissingHistory ?? "Keyboard artwork was unavailable in this interval.", Flattened: burned,
                InputIndexPath: inputPath, Keys: settings.KeyboardKeys?.Select(cap => new ClipOverlayKeyCap(cap.Code, cap.Label, cap.Row, cap.Units)).ToArray(),
                SourceName: settings.KeyboardName, ShowMouse: settings.KeyboardShowMouse);
        var changes = new List<ClipOverlayState>();
        OverlayCaptureSettings? previousSettings = null;
        foreach (var mapping in overlayMap)
        {
            var initial = states.LastOrDefault(state => state.AtUs <= mapping.SourceStartUs);
            if (initial is not null && (changes.Count == 0 || !Equals(initial.Settings, previousSettings)))
            {
                changes.Add(new(mapping.OutputStartUs / 1_000_000d, Camera(initial.Settings), Keyboard(initial.Settings)));
                previousSettings = initial.Settings;
            }
            foreach (var state in states.Where(state => state.AtUs > mapping.SourceStartUs && state.AtUs < mapping.SourceStartUs + mapping.DurationUs))
            {
                changes.Add(new((mapping.OutputStartUs + state.AtUs - mapping.SourceStartUs) / 1_000_000d, Camera(state.Settings), Keyboard(state.Settings)));
                previousSettings = state.Settings;
            }
        }
        var manifest = new ClipOverlayManifest(ClipOverlayManifest.CurrentVersion, Camera(cameraSettings), Keyboard(keyboardSettings), changes.OrderBy(state => state.StartSeconds).ToArray());
        ClipInfoSidecar.Save(root, output, new ClipInfo(game, null, title, File.GetCreationTimeUtc(output), CaptureSource: configuration.CaptureSource, OverlayManifest: manifest));
        WriteJson(root, LibraryLayout.SidecarPath(root, output, ".source.json"), new SpotifySourceWindow(audioMap[0].SourceStartUs / 1_000_000d,
            durationUs / 1_000_000d, MonotonicClock.BootId, audioMap.Select(map => new SpotifySourceMapping(map.SourceStartUs / 1_000_000d,
                map.DurationUs / 1_000_000d, map.OutputStartUs / 1_000_000d)).ToArray()));
    }
    internal static InputCaptureIndex RebaseInput(InputCaptureIndex input, long inputStartUs, IReadOnlyList<Mapping> mappings)
    {
        var edges = input.Transitions.OrderBy(edge => edge.Seconds).ToArray();
        var checkpoints = input.Checkpoints.OrderBy(checkpoint => checkpoint.Seconds).ToArray();
        var outputEdges = new List<InputTransition>(); var outputCheckpoints = new List<InputCheckpoint>();
        double lastCheckpoint = double.NegativeInfinity; long previousSourceEnd = long.MinValue;
        foreach (var map in mappings)
        {
            var sourceStart = (map.SourceStartUs - inputStartUs) / 1_000_000d;
            var sourceEnd = sourceStart + map.DurationUs / 1_000_000d;
            var outputStart = map.OutputStartUs / 1_000_000d;
            var checkpoint = checkpoints.LastOrDefault(item => item.Seconds <= sourceStart);
            var held = checkpoint is null ? new HashSet<InputPhysicalKey>() : new(checkpoint.Down);
            var from = checkpoint?.Seconds ?? double.NegativeInfinity;
            var edgeStart = LowerBound(edges, from, strict: true);
            var boundaryStart = LowerBound(edges, sourceStart, strict: false);
            var index = edgeStart;
            for (; index < edges.Length && edges[index].Seconds <= sourceStart; ++index)
            { if (edges[index].Down) held.Add(edges[index].Key); else held.Remove(edges[index].Key); }
            if (outputStart - lastCheckpoint >= 2 || previousSourceEnd != map.SourceStartUs)
            { outputCheckpoints.Add(new(outputStart, held.ToArray())); lastCheckpoint = outputStart; }
            for (index = boundaryStart; index < edges.Length && edges[index].Seconds < sourceEnd; ++index)
                outputEdges.Add(edges[index] with { Seconds = outputStart + edges[index].Seconds - sourceStart });
            previousSourceEnd = map.SourceStartUs + map.DurationUs;
        }
        return input with { Transitions = outputEdges, Checkpoints = outputCheckpoints };
        static int LowerBound(InputTransition[] values, double seconds, bool strict)
        {
            var left = 0; var right = values.Length;
            while (left < right)
            {
                var middle = left + (right - left) / 2;
                if (values[middle].Seconds < seconds || strict && values[middle].Seconds == seconds) left = middle + 1;
                else right = middle;
            }
            return left;
        }
    }
    private static IReadOnlyList<Mapping> Mappings(JsonElement result, string name)
    {
        var maps = result.GetProperty(name).EnumerateArray().Select(value => new Mapping(value.GetProperty("sourceStartUs").GetInt64(),
            value.GetProperty("durationUs").GetInt64(), value.GetProperty("outputStartUs").GetInt64())).Where(value => value.DurationUs > 0).OrderBy(value => value.OutputStartUs);
        var merged = new List<Mapping>();
        foreach (var map in maps)
        {
            if (merged.LastOrDefault() is { } previous && previous.SourceStartUs + previous.DurationUs == map.SourceStartUs && previous.OutputStartUs + previous.DurationUs == map.OutputStartUs)
                merged[^1] = previous with { DurationUs = previous.DurationUs + map.DurationUs };
            else merged.Add(map);
        }
        return merged;
    }
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    private static void RequireOwnedPath(string root, string path)
    {
        if (!LibraryPathGuard.IsWithin(root, path)) throw new InvalidDataException("Recording publication crosses a filesystem link.");
    }
    private static void WriteJson<T>(string root, string path, T value)
    {
        RequireOwnedPath(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
