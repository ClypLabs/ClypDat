using System.Globalization;

namespace ClypDat.App.ViewModels;

/// <summary>
/// What a settings section shows while a ClypDat kill switch has its feature
/// paused (Controls/PolicyPauseBanner). Built from the switch's reason and
/// expiry; the user's own setting is left alone and applies again afterwards.
/// </summary>
public sealed record PolicyPause(string Heading, string Reason, string Until)
{
    public static PolicyPause From(string feature, string reason, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var local = expiresAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        var when = local.Date == today ? $"today at {time}"
            : local.Date == today.AddDays(1) ? $"tomorrow at {time}"
            : $"{local.ToString("d MMM", CultureInfo.InvariantCulture)} at {time}";
        return new PolicyPause(
            $"{feature} is paused by ClypDat",
            string.IsNullOrWhiteSpace(reason) ? "Temporarily turned off for everyone." : reason.Trim(),
            $"Turns back on automatically {when}. Your settings are kept.");
    }
}
