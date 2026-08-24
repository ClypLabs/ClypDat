namespace ClypDat.Core.Settings;

public static class ReplayFrameRatePolicy
{
    public static IReadOnlyList<int> Selectable { get; } = new[] { 30, 60, 90, 120 };

    public static int NormalizePersisted(int frameRate) =>
        Math.Clamp(frameRate <= 0 ? 60 : frameRate, 30, 120);
}
