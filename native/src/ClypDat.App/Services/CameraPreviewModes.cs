using System.Globalization;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

internal static class CameraPreviewModeProbe
{
    private static readonly Regex Option = new(@"(?:(?<kind>pixel_format|vcodec)=(?<format>[^\s]+)).*?(?:min\s+s=(?<minWidth>\d+)x(?<minHeight>\d+)\s+fps=(?<minFps>[\d.]+))?.*?max\s+s=(?<width>\d+)x(?<height>\d+)\s+fps=(?<fps>[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<CameraPreviewMode> Parse(string output)
    {
        var modes = Option.Matches(output).Select(match => new CameraPreviewMode(
            int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["fps"].Value, CultureInfo.InvariantCulture), match.Groups["format"].Value,
            match.Groups["kind"].Value.Equals("vcodec", StringComparison.OrdinalIgnoreCase)))
            .Where(mode => mode.FramesPerSecond > 0).Distinct().ToList();
        return modes.OrderByDescending(mode => RateBand(mode.FramesPerSecond)).ThenByDescending(mode => mode.FramesPerSecond)
            .ThenBy(mode => CoversPreview(mode) ? 0 : 1).ThenBy(mode => mode.Width * mode.Height)
            .ThenBy(mode => mode.Format.Equals("nv12", StringComparison.OrdinalIgnoreCase) ? 0 : mode.IsCompressed ? 2 : 1).ToArray();
    }
    private static int RateBand(double rate) => rate >= 59 ? 3 : rate >= 29 ? 2 : 1;
    private static bool CoversPreview(CameraPreviewMode mode) => mode.Width >= CameraPreviewService.Width && mode.Height >= CameraPreviewService.Height;
}
