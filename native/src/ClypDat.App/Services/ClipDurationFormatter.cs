namespace ClypDat.App.Services;

/// <summary>
/// How a clip's length is written wherever it is shown.
///
/// The rule lived in three places and one of them used "m\:ss", which is the
/// MINUTES COMPONENT, not the total: a three-hour session recording rendered on
/// its library tile as "14:06". Hours have to be built from
/// <see cref="TimeSpan.TotalHours"/> rather than the "h" specifier too, or a
/// clip past twenty-four hours would wrap back to zero.
/// </summary>
public static class ClipDurationFormatter
{
    public static string Format(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0:00";
        var hours = (int)duration.TotalHours;
        return hours >= 1
            ? $"{hours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }
}
