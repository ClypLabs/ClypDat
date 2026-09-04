using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

internal sealed record ClipOverlayFrame(int Width, int Height, byte[] Pixels);

// Rasterize once, synchronously, using the same fonts and theme as the app.
// Only premultiplied pixels cross to the native animation thread.
internal static class ClipOverlayCardRenderer
{
    public static unsafe ClipOverlayFrame Render(ClipOverlayPresentation presentation)
    {
        Dispatcher.UIThread.VerifyAccess();
        var application = Application.Current ?? throw new InvalidOperationException("Overlay rendering requires the application.");
        var font = Resource<FontFamily>(application, "ClypDatFontFamily") ?? new FontFamily("fonts:Inter#Inter, $Default");
        var background = Resource<IBrush>(application, "SurfaceRaisedBrush") ?? Brushes.Black;
        var titleBrush = Resource<IBrush>(application, "TextStrongBrush") ?? Brushes.White;
        var detailBrush = Resource<IBrush>(application, "TextSubtleBrush") ?? Brushes.LightGray;
        var accent = Resource<IBrush>(application,
            presentation.Event.Kind == ClipOverlayKind.Failure ? "DangerBrush" : "AccentBrush") ?? titleBrush;
        var scale = Math.Clamp(presentation.Event.Target.Scaling, 0.75, 4);
        var width = Math.Min(300, Math.Max(80, presentation.Event.Target.WorkArea.Width / scale));
        var textWidth = Math.Max(1, width - 74);
        using var title = new TextLayout(presentation.Event.Title, new Typeface(font, weight: FontWeight.Medium),
            18, titleBrush, textWrapping: TextWrapping.Wrap, maxWidth: textWidth);
        using var detail = new TextLayout(presentation.Event.Detail, new Typeface(font),
            16, detailBrush, textWrapping: TextWrapping.Wrap, maxWidth: textWidth);
        var hasDetail = !string.IsNullOrWhiteSpace(presentation.Event.Detail);
        var textHeight = title.Height + (hasDetail ? 4 + detail.Height : 0);
        var height = Math.Max(66, textHeight + 20);
        var size = new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
        var dpi = new Vector(96 * scale, 96 * scale);
        using var bitmap = new RenderTargetBitmap(size, dpi);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, width, height), 8, 8);
            context.DrawRectangle(accent, null, new Rect(0, 8, 4, height - 16));
            context.DrawImage(AppThemeService.CurrentLogo(large: true), new Rect(16, (height - 28) / 2, 28, 28));
            var top = (height - textHeight) / 2;
            title.Draw(context, new Point(58, top));
            if (hasDetail) detail.Draw(context, new Point(58, top + title.Height + 4));
        }

        var pixels = new byte[checked(size.Width * size.Height * 4)];
        fixed (byte* pointer = pixels)
        {
            using var buffer = new LockedFramebuffer((nint)pointer, size, size.Width * 4, dpi,
                PixelFormat.Bgra8888, AlphaFormat.Premul, null);
            bitmap.CopyPixels(buffer);
        }
        return new ClipOverlayFrame(size.Width, size.Height, pixels);
    }

    private static T? Resource<T>(Application application, string key) where T : class
        => application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value) ? value as T : null;
}
