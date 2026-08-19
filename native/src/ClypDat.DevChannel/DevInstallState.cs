using System.Text.Json;

namespace ClypDat.DevChannel;

public sealed record DevInstallState(string? CurrentBuildId, string? PreviousBuildId, string? PendingBuildId)
{
    public static DevInstallState Empty { get; } = new(null, null, null);
}

public static class DevInstallStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static DevInstallState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return DevInstallState.Empty;
            var state = JsonSerializer.Deserialize<DevInstallState>(File.ReadAllText(path));
            return state is not null && IsSafe(state.CurrentBuildId) && IsSafe(state.PreviousBuildId) && IsSafe(state.PendingBuildId)
                ? state
                : DevInstallState.Empty;
        }
        catch { return DevInstallState.Empty; }
    }

    public static void SaveAtomic(string path, DevInstallState state)
    {
        if (!IsSafe(state.CurrentBuildId) || !IsSafe(state.PreviousBuildId) || !IsSafe(state.PendingBuildId))
            throw new InvalidDataException("Dev install state contains an unsafe build id.");
        var folder = Path.GetDirectoryName(path) ?? throw new ArgumentException("State path must have a directory.", nameof(path));
        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsSafe(string? value) => value is null ||
        (value.Length is > 0 and <= 128 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'));
}
