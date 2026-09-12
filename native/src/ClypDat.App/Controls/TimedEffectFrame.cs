using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClypDat.App.Controls;

public sealed class TimedEffectFrame : Control
{
    public Bitmap? Bitmap { get; set; }
    public Rect SourceFraction { get; set; } = new(0, 0, 1, 1);
    public override void Render(DrawingContext context)
    {
        if (Bitmap is not { } bitmap) return;
        context.DrawImage(bitmap, new Rect(SourceFraction.X * bitmap.Size.Width, SourceFraction.Y * bitmap.Size.Height,
            SourceFraction.Width * bitmap.Size.Width, SourceFraction.Height * bitmap.Size.Height), new Rect(Bounds.Size));
    }
}
