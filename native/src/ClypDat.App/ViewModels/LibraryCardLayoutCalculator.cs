namespace ClypDat.App.ViewModels;

internal readonly record struct LibraryCardLayout(int Columns, double Width, double ImageHeight);

internal static class LibraryCardLayoutCalculator
{
    internal const double HorizontalMargin = 24;
    private const double MinimumContentWidth = 320;
    private const double MinimumCardWidth = 220;
    private const double ScaledCardTargetWidth = 400;
    private const double SafetyReserve = 1;

    public static LibraryCardLayout Calculate(double viewportWidth, bool scaleWithWindow)
    {
        var contentWidth = Math.Max(MinimumContentWidth, viewportWidth);
        // Keep thresholds based on unreserved width. Reserve one DIP only
        // after choosing columns, preventing fractional-scale overflow.
        var columns = scaleWithWindow
            ? Math.Clamp((int)Math.Floor(contentWidth / ScaledCardTargetWidth), 2, 10)
            : 3;
        var width = Math.Max(MinimumCardWidth,
            Math.Floor((contentWidth - SafetyReserve) / columns) - HorizontalMargin);

        return new LibraryCardLayout(columns, width, Math.Floor(width * 9 / 16));
    }
}
