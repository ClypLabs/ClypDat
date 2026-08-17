using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class ReplayBufferConfigIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ReplayBufferConfig? config)
        => config is null ? string.Empty : JsonSerializer.Serialize(config, JsonOptions);
}
