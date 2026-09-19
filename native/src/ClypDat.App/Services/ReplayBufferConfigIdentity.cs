using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class ReplayBufferConfigIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ReplayBufferConfig? config)
        // Format is a next-session preference, not a reason to interrupt an
        // attached writer. Attach still updates the worker's next-start config.
        => config is null ? string.Empty : JsonSerializer.Serialize(config with { FullSessionContainer = "MKV" }, JsonOptions);
}
