namespace ClypDat.App.Services;

/// <summary>
/// What the correlation is run on. <see cref="Raw"/> correlates the greyscale
/// crops as captured. <see cref="HighPass3"/> subtracts a radius-3 box blur from
/// both sides first, so only detail finer than the blur survives.
/// </summary>
public enum TemplateScoring
{
    Raw,
    HighPass3
}

/// <summary>
/// Brightness-invariant normalized correlation between a reference crop and a
/// live one, both already greyscale.
///
/// This exists because Windows OCR cannot read the games' stylised banners at
/// all - "TRIPLE KILL" comes back as "&amp;fR/PlfWlEö", "TEAM KILL!" and "VICTORY
/// ROYALE" as nothing, and upscaling, contrast and inversion do not help. The
/// banners are drawn from fixed art at a fixed place, so correlating against a
/// reference separates them cleanly where reading them does not.
///
/// <see cref="TemplateScoring.Raw"/> correlates the crops directly, which works
/// only where the banner fills its region. Overwatch's streak banners do not:
/// they are text on a semi-transparent plate, the templates were cut from live
/// frames and carry gameplay bleed, and a 300x48 correlation is dominated by low
/// spatial frequencies. Measured against 402 banner-free frames from a tester's
/// clips, the quintuple template scored up to 0.781 on plain scenery - above the
/// 0.7 threshold and above the 0.673 that real banners scored - so 5% of ALL
/// frames fired an event. Neither a higher threshold nor a runner-up margin
/// separates those bands: on real double-kill frames double leads triple by
/// 0.19, and on empty scenery quintuple leads double by 0.16.
///
/// <see cref="TemplateScoring.HighPass3"/> is what does separate them. Banner
/// text is thin bright strokes; scenery is smooth gradient. Removing the low
/// frequencies from both sides drops those same false matches to 0.116 while
/// real banners stay at 0.305 and above, and every one of 15 hand-labelled
/// banner frames then picks its own template. Detail is what the capture loses
/// first, so the margin narrows with resolution - see
/// <c>IsSupportedDetectorResolution</c> in NativeReplayBuffer for the floor.
///
/// <see cref="FixedRegionTemplateMatcher"/> does the same maths against image
/// files on disk; this one works on the in-memory crops the detector pipeline
/// actually carries, and rescales so a template captured at 1080p still matches
/// a 1440p capture.
/// </summary>
public sealed class GrayTemplateMatcher
{
    private const int HighPassRadius = 3;

    private readonly int _width;
    private readonly int _height;
    private readonly double[] _centered;
    private readonly double _magnitude;
    private readonly TemplateScoring _scoring;

    private GrayTemplateMatcher(int width, int height, double[] centered, double magnitude, TemplateScoring scoring)
    {
        _width = width;
        _height = height;
        _centered = centered;
        _magnitude = magnitude;
        _scoring = scoring;
    }

    public static GrayTemplateMatcher FromGray(GrayDetectorImage template, TemplateScoring scoring = TemplateScoring.Raw)
    {
        if (template.Width <= 0 || template.Height <= 0 || template.Pixels.Length != template.Width * template.Height)
            throw new InvalidDataException("Template image dimensions are invalid.");
        var prepared = Prepare(template.Pixels, template.Width, template.Height, scoring);
        var mean = prepared.Average();
        var centered = prepared.Select(value => value - mean).ToArray();
        return new GrayTemplateMatcher(template.Width, template.Height, centered, Math.Sqrt(centered.Sum(value => value * value)), scoring);
    }

    /// <summary>
    /// 1 is identical, 0 is unrelated. Returns 0 for a flat candidate, where
    /// correlation is undefined rather than perfect.
    /// </summary>
    public double Score(GrayDetectorImage candidate)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0) return 0;
        // Resample first, then filter: the high pass has to run at the same
        // scale on both sides or its cutoff means something different for each.
        var prepared = Prepare(Resample(candidate, _width, _height), _width, _height, _scoring);
        var mean = prepared.Average();
        double dot = 0;
        double magnitudeSquared = 0;
        for (var index = 0; index < prepared.Length; index++)
        {
            var centered = prepared[index] - mean;
            dot += centered * _centered[index];
            magnitudeSquared += centered * centered;
        }
        var denominator = _magnitude * Math.Sqrt(magnitudeSquared);
        return denominator <= double.Epsilon ? 0 : Math.Clamp(dot / denominator, -1, 1);
    }

    private static double[] Prepare(byte[] pixels, int width, int height, TemplateScoring scoring)
    {
        var values = new double[pixels.Length];
        for (var index = 0; index < pixels.Length; index++) values[index] = pixels[index];
        return scoring == TemplateScoring.HighPass3 ? HighPass(values, width, height, HighPassRadius) : values;
    }

    /// <summary>
    /// The image minus its box blur, edges clamped. Separable running sums, so
    /// the radius costs nothing: two passes over the window regardless of size.
    /// </summary>
    private static double[] HighPass(double[] values, int width, int height, int radius)
    {
        var blurred = BoxBlur(values, width, height, radius);
        var output = new double[values.Length];
        for (var index = 0; index < values.Length; index++) output[index] = values[index] - blurred[index];
        return output;
    }

    private static double[] BoxBlur(double[] values, int width, int height, int radius)
    {
        var window = 2 * radius + 1;
        var horizontal = new double[values.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            double sum = 0;
            for (var offset = -radius; offset <= radius; offset++) sum += values[row + Math.Clamp(offset, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                horizontal[row + x] = sum / window;
                sum -= values[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += values[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }

        var output = new double[values.Length];
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            for (var offset = -radius; offset <= radius; offset++) sum += horizontal[Math.Clamp(offset, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                output[y * width + x] = sum / window;
                sum -= horizontal[Math.Clamp(y - radius, 0, height - 1) * width + x];
                sum += horizontal[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
            }
        }
        return output;
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
