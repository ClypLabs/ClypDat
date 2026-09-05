namespace ClypDat.App.Services;

/// <summary>
/// Brightness-invariant normalized correlation between a reference crop and a
/// live one, both already greyscale.
///
/// This exists because Windows OCR cannot read the games' stylised banners at
/// all - "TRIPLE KILL" comes back as "&amp;fR/PlfWlEö", "TEAM KILL!" and "VICTORY
/// ROYALE" as nothing, and upscaling, contrast and inversion do not help. The
/// banners are drawn from fixed art at a fixed place, so correlating against a
/// reference separates them cleanly where reading them does not: a matching
/// banner scores ~0.97, a different banner in the same spot ~0.50, and an empty
/// HUD ~0.14.
///
/// <see cref="FixedRegionTemplateMatcher"/> does the same maths against image
/// files on disk; this one works on the in-memory crops the detector pipeline
/// actually carries, and rescales so a template captured at 1080p still matches
/// a 1440p capture.
/// </summary>
public sealed class GrayTemplateMatcher
{
    private readonly int _width;
    private readonly int _height;
    private readonly double[] _centered;
    private readonly double _magnitude;

    private GrayTemplateMatcher(int width, int height, double[] centered, double magnitude)
    {
        _width = width;
        _height = height;
        _centered = centered;
        _magnitude = magnitude;
    }

    public static GrayTemplateMatcher FromGray(GrayDetectorImage template)
    {
        if (template.Width <= 0 || template.Height <= 0 || template.Pixels.Length != template.Width * template.Height)
            throw new InvalidDataException("Template image dimensions are invalid.");
        var mean = template.Pixels.Average(value => (double)value);
        var centered = template.Pixels.Select(value => value - mean).ToArray();
        return new GrayTemplateMatcher(template.Width, template.Height, centered, Math.Sqrt(centered.Sum(value => value * value)));
    }

    /// <summary>
    /// 1 is identical, 0 is unrelated. Returns 0 for a flat candidate, where
    /// correlation is undefined rather than perfect.
    /// </summary>
    public double Score(GrayDetectorImage candidate)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0) return 0;
        var resampled = Resample(candidate, _width, _height);
        var mean = resampled.Average(value => (double)value);
        double dot = 0;
        double magnitudeSquared = 0;
        for (var index = 0; index < resampled.Length; index++)
        {
            var centered = resampled[index] - mean;
            dot += centered * _centered[index];
            magnitudeSquared += centered * centered;
        }
        var denominator = _magnitude * Math.Sqrt(magnitudeSquared);
        return denominator <= double.Epsilon ? 0 : Math.Clamp(dot / denominator, -1, 1);
    }

    /// <summary>
    /// Best score with the template slid down a taller search band.
    ///
    /// Banners do not sit at a fixed height: Fortnite stacks them, so
    /// "ELIMINATION!" alone lands ~20px lower than "DOUBLE ELIM!" with an
    /// "ENEMY TEAM WIPED!" above it, and Overwatch's Play of the Game banner
    /// moves hundreds of pixels between matches. Searching vertically costs one
    /// correlation per step and removes the whole class of "the banner was 20px
    /// off so nothing matched".
    /// </summary>
    public double ScoreBest(GrayDetectorImage candidate, int steps = 12)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0) return 0;
        // Scale the template to the candidate's width, then walk it down.
        var windowHeight = Math.Max(1, (int)Math.Round(_height * (candidate.Width / (double)_width)));
        if (windowHeight >= candidate.Height) return Score(candidate);

        var travel = candidate.Height - windowHeight;
        var best = 0.0;
        for (var step = 0; step <= steps; step++)
        {
            var top = (int)Math.Round(travel * (step / (double)steps));
            var window = new byte[candidate.Width * windowHeight];
            Array.Copy(candidate.Pixels, top * candidate.Width, window, 0, window.Length);
            var score = Score(new GrayDetectorImage(candidate.Width, windowHeight, window));
            if (score > best) best = score;
        }
        return best;
    }

    /// <summary>Nearest-neighbour is enough: these crops are already close in size.</summary>
    private static byte[] Resample(GrayDetectorImage image, int width, int height)
    {
        if (image.Width == width && image.Height == height) return image.Pixels;
        var output = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(image.Height - 1, y * image.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(image.Width - 1, x * image.Width / width);
                output[y * width + x] = image.Pixels[sourceY * image.Width + sourceX];
            }
        }
        return output;
    }

    /// <summary>
    /// Crops a sub-rectangle expressed in 0..1 of the given image, for pulling a
    /// banner out of the larger HUD slot it was captured in.
    /// </summary>
    public static GrayDetectorImage Crop(GrayDetectorImage image, NormalizedRegion region)
    {
        var rect = region.ToPixelRect(image.Width, image.Height);
        var pixels = new byte[rect.Width * rect.Height];
        for (var row = 0; row < rect.Height; row++)
        {
            Array.Copy(image.Pixels, (rect.Y + row) * image.Width + rect.X, pixels, row * rect.Width, rect.Width);
        }
        return new GrayDetectorImage(rect.Width, rect.Height, pixels);
    }

    /// <summary>
    /// Re-expresses a frame-relative rectangle inside a slot that was itself
    /// cropped from the frame. Template rectangles are measured against the full
    /// frame - one coordinate system for every measurement - and converted here.
    /// </summary>
    public static NormalizedRegion ToSlotRelative(NormalizedRegion frameRegion, NormalizedRegion slotRegion)
    {
        var x = (frameRegion.X - slotRegion.X) / slotRegion.Width;
        var y = (frameRegion.Y - slotRegion.Y) / slotRegion.Height;
        return new NormalizedRegion(
            Math.Clamp(x, 0, 1),
            Math.Clamp(y, 0, 1),
            Math.Clamp(frameRegion.Width / slotRegion.Width, 0, 1 - Math.Clamp(x, 0, 1)),
            Math.Clamp(frameRegion.Height / slotRegion.Height, 0, 1 - Math.Clamp(y, 0, 1)));
    }
}
