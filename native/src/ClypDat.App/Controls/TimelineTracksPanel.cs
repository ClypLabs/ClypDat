using Avalonia;
using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

// The video strip needs less vertical room than an audio waveform. Keep both
// timeline columns on the same layout rule so labels, lanes and chrome stay
// perfectly aligned as tracks are added.
public sealed class TimelineTracksPanel : Panel
{
    private const double VideoLaneHeight = 42;
    private const double MinimumLaneHeight = 34;

    protected override Size MeasureOverride(Size availableSize)
    {
        var heights = GetLaneHeights(availableSize.Height);
        var widest = 0d;
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Measure(new Size(availableSize.Width, heights[index]));
            widest = Math.Max(widest, Children[index].DesiredSize.Width);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? heights.Sum() : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var heights = GetLaneHeights(finalSize.Height);
        var y = 0d;
        for (var index = 0; index < Children.Count; index++)
        {
            var height = heights[index];
            Children[index].Arrange(new Rect(0, y, finalSize.Width, height));
            y += height;
        }
        return finalSize;
    }

    private double[] GetLaneHeights(double availableHeight)
    {
        var count = Children.Count;
        if (count == 0) return [];
        if (double.IsInfinity(availableHeight)) availableHeight = count * 64;

        var videoIndex = -1;
        for (var index = 0; index < count; index++)
        {
            if (Children[index].DataContext is TrackLaneViewModel { IsVideo: true })
            {
                videoIndex = index;
                break;
            }
        }

        var heights = new double[count];
        if (videoIndex < 0 || availableHeight < VideoLaneHeight + (count - 1) * MinimumLaneHeight)
        {
            Array.Fill(heights, availableHeight / count);
            return heights;
        }

        heights[videoIndex] = VideoLaneHeight;
        var audioHeight = (availableHeight - VideoLaneHeight) / (count - 1);
        for (var index = 0; index < count; index++)
        {
            if (index != videoIndex) heights[index] = audioHeight;
        }
        return heights;
    }
}
