using System.Diagnostics;

namespace ClypDat.App.Services;

public sealed record ProcessPriorityOption(string Label, string Value);

public static class ProcessPriorityService
{
    public static readonly IReadOnlyList<ProcessPriorityOption> Options =
    [
        new("Idle", "Idle"),
        new("Below normal", "BelowNormal"),
        new("Normal", "Normal"),
        new("Above normal", "AboveNormal"),
        new("High", "High")
    ];

    public static string Normalize(string? value) => value switch
    {
        "Idle" or "BelowNormal" or "Normal" or "AboveNormal" or "High" => value,
        _ => "High"
    };

    public static void Apply(string? value)
    {
        var normalized = Normalize(value);
        var priority = normalized switch
        {
            "Idle" => ProcessPriorityClass.Idle,
            "BelowNormal" => ProcessPriorityClass.BelowNormal,
            "Normal" => ProcessPriorityClass.Normal,
            "AboveNormal" => ProcessPriorityClass.AboveNormal,
            _ => ProcessPriorityClass.High
        };

        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = priority;
            AppLog.Info($"Process priority set to {normalized}.");
        }
        catch (Exception error)
        {
            // Priority is useful under CPU contention, but capture must still
            // start if Windows declines the requested class.
            AppLog.Error($"Failed to set process priority to {normalized}.", error);
        }
    }
}
