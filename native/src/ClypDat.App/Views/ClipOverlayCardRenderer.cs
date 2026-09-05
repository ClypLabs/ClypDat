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
    // The badge shape: a full-height accent rail against the screen edge, the
    // app mark, a bold title, and the save hotkey as keycap chips. Sized to its
    // content rather than to a fixed width - a game name is as long as it is,
    // and wrapping "HELLDIVERS 2" onto a second line reads as a defect.
    private const double RailWidth = 5;
    private const double PadLeft = 18, PadTop = 16, PadRight = 24, PadBottom = 16;
    private const double LogoSize = 26, LogoGap = 14;
    private const double TitleSize = 17, DetailSize = 13, ChipTextSize = 13;
    private const double TitleMaxWidth = 340;
    private const double RowGap = 7;
    private const double ChipPadX = 7, ChipPadTop = 2, ChipPadBottom = 3, ChipRadius = 4;
    private const double ChipSpacing = 6, TrailingGap = 2;
    private const double MinWidth = 220, MaxWidth = 380, MinHeight = 58;
    private const double CardRadius = 8;
    // Near-opaque rather than solid: enough gameplay bleeds through that the
    // badge sits in the frame instead of punching a hole in it.
    private const double FillOpacity = 0.96;

    public static unsafe ClipOverlayFrame Render(ClipOverlayPresentation presentation)
    {
        Dispatcher.UIThread.VerifyAccess();
        var application = Application.Current ?? throw new InvalidOperationException("Overlay rendering requires the application.");
        var font = Resource<FontFamily>(application, "ClypDatFontFamily") ?? new FontFamily("fonts:Inter#Inter, $Default");
        var background = Resource<IBrush>(application, "SurfaceBrush") ?? Brushes.Black;
        var edge = Resource<IBrush>(application, "EdgeStrongBrush") ?? Brushes.Gray;
        var titleBrush = Resource<IBrush>(application, "TextStrongBrush") ?? Brushes.White;
        var detailBrush = Resource<IBrush>(application, "TextSubtleBrush") ?? Brushes.LightGray;
        var chipFill = Resource<IBrush>(application, "SurfaceHoverBrush") ?? Brushes.DimGray;
        var chipTextBrush = Resource<IBrush>(application, "TextBrush") ?? Brushes.White;
        var accent = Resource<IBrush>(application,
            presentation.Event.Kind == ClipOverlayKind.Failure ? "DangerBrush" : "AccentBrush") ?? titleBrush;

        var body = new Typeface(font);
        var bold = new Typeface(font, weight: FontWeight.Bold);
        var scale = Math.Clamp(presentation.Event.Target.Scaling, 0.75, 4);
        var chrome = RailWidth + PadLeft + LogoSize + LogoGap + PadRight;
        var ceiling = Math.Max(MinWidth, Math.Min(MaxWidth, presentation.Event.Target.WorkArea.Width / scale));

        var hint = HintRow.Build(presentation.Event, bold, body, chipTextBrush, detailBrush);
        var hasDetail = !string.IsNullOrWhiteSpace(presentation.Event.Detail);
        try
        {
            var column = TitleMaxWidth;
            var title = new TextLayout(presentation.Event.Title, bold, TitleSize, titleBrush,
                textWrapping: TextWrapping.Wrap, maxWidth: column);
            var detail = new TextLayout(hasDetail ? presentation.Event.Detail : string.Empty, body, DetailSize,
                detailBrush, textWrapping: TextWrapping.Wrap, maxWidth: column);
            try
            {
                var measured = Math.Max(title.Width, Math.Max(hasDetail ? detail.Width : 0, hint.Width));
                var width = Math.Clamp(chrome + measured, MinWidth, ceiling);
                // The clamp only bites on a narrow monitor or an unusually long
                // title; when it does, the text has to be re-wrapped at the
                // column that actually survived.
                var available = Math.Max(1, width - chrome);
                if (available < measured - 0.5)
                {
                    column = available;
                    title.Dispose();
                    detail.Dispose();
                    title = new TextLayout(presentation.Event.Title, bold, TitleSize, titleBrush,
                        textWrapping: TextWrapping.Wrap, maxWidth: column);
                    detail = new TextLayout(hasDetail ? presentation.Event.Detail : string.Empty, body, DetailSize,
                        detailBrush, textWrapping: TextWrapping.Wrap, maxWidth: column);
                }

                var rows = title.Height
                    + (hasDetail ? RowGap + detail.Height : 0)
                    + (hint.HasContent ? RowGap + hint.Height : 0);
                var content = Math.Max(LogoSize, rows);
                var height = Math.Max(MinHeight, PadTop + content + PadBottom);

                var size = new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
                var dpi = new Vector(96 * scale, 96 * scale);
                using var bitmap = new RenderTargetBitmap(size, dpi);
                using (var context = bitmap.CreateDrawingContext())
                {
                    var left = presentation.Event.Placement is ClipOverlayPlacement.TopLeft
                        or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
                    // Square at the screen edge, rounded on the exposed side - a
                    // fully rounded badge sitting flush reads as a gap, because
                    // the curve pulls the fill away from the edge at the corners.
                    var corners = left
                        ? new CornerRadius(0, CardRadius, CardRadius, 0)
                        : new CornerRadius(CardRadius, 0, 0, CardRadius);
                    using (context.PushOpacity(FillOpacity))
                        context.DrawRectangle(background, null, new RoundedRect(new Rect(0, 0, width, height), corners));
                    context.DrawRectangle(null, new Pen(edge, 1), new RoundedRect(new Rect(0.5, 0.5, width - 1, height - 1), corners));
                    context.DrawRectangle(accent, null, new Rect(left ? 0 : width - RailWidth, 0, RailWidth, height));

                    var contentTop = (height - content) / 2;
                    var logoX = (left ? RailWidth : 0) + PadLeft;
                    context.DrawImage(AppThemeService.CurrentLogo(large: true),
                        new Rect(logoX, contentTop + (content - LogoSize) / 2, LogoSize, LogoSize));

                    var textX = logoX + LogoSize + LogoGap;
                    var y = contentTop + (content - rows) / 2;
                    title.Draw(context, new Point(textX, y));
                    y += title.Height;
                    if (hasDetail)
                    {
                        y += RowGap;
                        detail.Draw(context, new Point(textX, y));
                        y += detail.Height;
                    }

                    if (hint.HasContent) hint.Draw(context, textX, y + RowGap, chipFill, edge);
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
            finally
            {
                title.Dispose();
                detail.Dispose();
            }
        }
        finally { hint.Dispose(); }
    }

    // "Ctrl+Shift+F9" -> [Ctrl] + [Shift] + [F9] as keycap chips, followed by
    // whatever the hint says the keys do. Built from the live setting rather
    // than hardcoded, so a rebound hotkey teaches the right keys.
    private readonly struct HintRow : IDisposable
    {
        private readonly TextLayout[] _keys;
        private readonly TextLayout[] _plus;
        private readonly TextLayout? _trailing;

        private HintRow(TextLayout[] keys, TextLayout[] plus, TextLayout? trailing, double width, double height)
        {
            _keys = keys;
            _plus = plus;
            _trailing = trailing;
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }
        public bool HasContent => _keys.Length > 0;

        public static HintRow Build(ClipOverlayEvent notification, Typeface bold, Typeface body, IBrush keyBrush, IBrush hintBrush)
        {
            if (string.IsNullOrWhiteSpace(notification.Hotkey))
                return new HintRow([], [], null, 0, 0);

            var names = notification.Hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0) return new HintRow([], [], null, 0, 0);

            var keys = new TextLayout[names.Length];
            var plus = new TextLayout[Math.Max(0, names.Length - 1)];
            for (var index = 0; index < names.Length; index++)
            {
                keys[index] = new TextLayout(names[index], bold, ChipTextSize, keyBrush);
                if (index > 0) plus[index - 1] = new TextLayout("+", body, ChipTextSize, hintBrush);
            }

            var trailing = string.IsNullOrWhiteSpace(notification.HotkeyHint)
                ? null
                : new TextLayout(notification.HotkeyHint, body, ChipTextSize, hintBrush);

            var width = 0d;
            var height = 0d;
            for (var index = 0; index < keys.Length; index++)
            {
                if (index > 0) width += ChipSpacing + plus[index - 1].Width + ChipSpacing;
                width += ChipWidth(keys[index]);
                height = Math.Max(height, ChipHeight(keys[index]));
            }

            if (trailing is not null)
            {
                width += TrailingGap + ChipSpacing + trailing.Width;
                height = Math.Max(height, trailing.Height);
            }

            return new HintRow(keys, plus, trailing, width, height);
        }

        public void Draw(DrawingContext context, double x, double y, IBrush fill, IBrush border)
        {
            for (var index = 0; index < _keys.Length; index++)
            {
                if (index > 0)
                {
                    var glyph = _plus[index - 1];
                    x += ChipSpacing;
                    glyph.Draw(context, new Point(x, y + (Height - glyph.Height) / 2));
                    x += glyph.Width + ChipSpacing;
                }

                var key = _keys[index];
                var chipWidth = ChipWidth(key);
                var chipHeight = ChipHeight(key);
                var chipTop = y + (Height - chipHeight) / 2;
                context.DrawRectangle(fill, new Pen(border, 1),
                    new RoundedRect(new Rect(x, chipTop, chipWidth, chipHeight), ChipRadius));
                key.Draw(context, new Point(x + 1 + ChipPadX, chipTop + 1 + ChipPadTop));
                x += chipWidth;
            }

            if (_trailing is null) return;
            x += TrailingGap + ChipSpacing;
            _trailing.Draw(context, new Point(x, y + (Height - _trailing.Height) / 2));
        }

        private static double ChipWidth(TextLayout key) => key.Width + (ChipPadX * 2) + 2;
        private static double ChipHeight(TextLayout key) => key.Height + ChipPadTop + ChipPadBottom + 2;

        public void Dispose()
        {
            foreach (var key in _keys) key.Dispose();
            foreach (var glyph in _plus) glyph.Dispose();
            _trailing?.Dispose();
        }
    }

    private static T? Resource<T>(Application application, string key) where T : class
        => application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value) ? value as T : null;
}
