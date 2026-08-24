namespace ClypDat.Core.Settings;

public static class ReplayFrameRatePolicy
{
    public const int Minimum = 30;
    public const int Maximum = 120;

    public static IReadOnlyList<int> Selectable { get; } = new[] { 30, 60, 90, 120 };

    public static int NormalizePersisted(int frameRate) =>
        Math.Clamp(frameRate <= 0 ? 60 : frameRate, Minimum, Maximum);
}
