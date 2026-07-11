using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Eve.App.Services;

namespace Eve.App.ViewModels;

public sealed class ClipCardViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isHovered;
    private MediaFileInfo _media;
    private string _previewImagePath;
    private Bitmap? _previewImage;
    private ClipInfo? _clipInfo;

    public ClipCardViewModel(MediaFileInfo media)
    {
        _media = media;
        _previewImagePath = media.ThumbnailPath;
        _clipInfo = ClipInfoSidecar.Load(media.Path);
        SetPreviewImage(_previewImagePath);
    }

    private static readonly Regex TrailingTimestampPattern = new(@"\s\d{4}-\d{2}-\d{2}\s\d{2}-\d{2}-\d{2}$", RegexOptions.Compiled);

    public MediaFileInfo Media => _media;
    public string Name => Media.Name;
    public string Path => Media.Path;
    public DateTimeOffset CreatedAt => Media.CreatedAt;
    public TimeSpan Duration => Media.Duration;
    public long SizeBytes => Media.SizeBytes;
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy h:mm tt");

    // Name is the filename ClipFileNaming.BuildFileName produced - the game/
    // auto-clip label plus a " yyyy-MM-dd HH-mm-ss" timestamp appended for
    // uniqueness on disk (e.g. "Marvel Rivals 2026-07-11 22-16-11"). Strip that
    // suffix back off for display; there's no separately stored game field.
    public string GameNameLabel => TrailingTimestampPattern.Replace(Name, string.Empty);

    public string ClipFromLabel => $"Clip from {CreatedAt:MMM d, yyyy}";

    // For a CS2 auto-clip, GameNameLabel is really "<event> - <map>" (e.g.
    // "3K - Mirage") since that's what the auto-clip title became when it was
    // used to build the filename - swap the tile around for those: lead with
    // the event/map (the interesting part) and show the actual game name
    // (from the sidecar, not parseable out of the filename) as the small label
    // above it instead of "Clip from <date>".
    public bool IsAutoClip => !string.IsNullOrWhiteSpace(_clipInfo?.AutoClipEventType);
    public string TileTopLabel => IsAutoClip ? (_clipInfo!.GameDisplayName ?? GameNameLabel) : GameNameLabel;
    public string TileMainLabel => IsAutoClip ? GameNameLabel : ClipFromLabel;

    private static readonly (string Prefix, IBrush Fill)[] AutoClipIconStyles =
    {
        ("Headshot", Brush.Parse("#E5A00D")),
        ("Death", Brush.Parse("#D85E61")),
        ("Assist", Brush.Parse("#5864E8"))
    };

    // Death/Assist/Headshot get their own icon+color; anything else (Kill, 2K,
    // 3K, 4K, Ace) falls back to a plain kill/target icon.
    public bool HasAutoClipIcon => IsAutoClip;

    public string AutoClipIconGeometry => _clipInfo?.AutoClipEventType switch
    {
        { } type when type.StartsWith("Headshot", StringComparison.OrdinalIgnoreCase) =>
            "M12,17.27L18.18,21l-1.64-7.03L22,9.24l-7.19-0.61L12,2L9.19,8.63L2,9.24l5.46,4.73L5.82,21z",
        "Death" =>
            "M12,2C6.47,2,2,6.47,2,12s4.47,10,10,10s10-4.47,10-10S17.53,2,12,2z M17,15.59L15.59,17L12,13.41L8.41,17L7,15.59L10.59,12L7,8.41L8.41,7L12,10.59L15.59,7L17,8.41L13.41,12L17,15.59z",
        "Assist" =>
            "M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10s10-4.48,10-10S17.52,2,12,2z M17,13h-4v4h-2v-4H7v-2h4V7h2v4h4V13z",
        _ =>
            "M12,8c-2.21,0-4,1.79-4,4s1.79,4,4,4s4-1.79,4-4S14.21,8,12,8L12,8z M20.94,11c-0.46-4.17-3.77-7.48-7.94-7.94V1h-2v2.06C6.83,3.52,3.52,6.83,3.06,11H1v2h2.06c0.46,4.17,3.77,7.48,7.94,7.94V23h2v-2.06c4.17-0.46,7.48-3.77,7.94-7.94H23v-2H20.94z M12,19c-3.87,0-7-3.13-7-7c0-3.87,3.13-7,7-7s7,3.13,7,7C19,15.87,15.87,19,12,19z"
    };

    public IBrush AutoClipIconFill
    {
        get
        {
            var type = _clipInfo?.AutoClipEventType;
            if (type is null) return Brush.Parse("#8C98A7");
            foreach (var (prefix, fill) in AutoClipIconStyles)
            {
                if (type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return fill;
            }

            return Brush.Parse("#8C98A7");
        }
    }

    // Relative for anything recent (matches how Medal/most clip tools show it -
    // "9 days ago" scans faster than a timestamp), falls back to an absolute date
    // once it's old enough that "X ago" stops being useful at a glance.
    public string RelativeDateLabel
    {
        get
        {
            var age = DateTimeOffset.Now - CreatedAt;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return "Just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min{((int)age.TotalMinutes == 1 ? "" : "s")} ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
            return CreatedAt.ToString("MMM d, yyyy");
        }
    }
    public string DurationLabel => Duration > TimeSpan.Zero ? Duration.ToString("m\\:ss") : "0:00";
    public string GameLabel => "VIDEO";
    public string CaptureBackendLabel => string.IsNullOrWhiteSpace(Media.CaptureBackend) ? string.Empty : $"Captured with: {Media.CaptureBackend}";
    public bool HasCaptureBackendLabel => !string.IsNullOrWhiteSpace(CaptureBackendLabel);

    public string PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (!SetProperty(ref _previewImagePath, value)) return;
            SetPreviewImage(value);
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    private int _selectionOrder;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
            OnPropertyChanged(nameof(SelectionBorderBrush));
            OnPropertyChanged(nameof(SelectionBorderThickness));
        }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (!SetProperty(ref _isHovered, value)) return;
            OnPropertyChanged(nameof(IsCheckVisible));
            OnPropertyChanged(nameof(SelectionBorderBrush));
            OnPropertyChanged(nameof(SelectionBorderThickness));
        }
    }

    // Set by MainWindowViewModel to reflect the order clips were selected in
    // (1-based; 0 = not selected), shown as a big number overlay like GG's
    // clip picker so a multi-select shows which clip you tapped 1st, 2nd, etc.
    public int SelectionOrder
    {
        get => _selectionOrder;
        set
        {
            if (!SetProperty(ref _selectionOrder, value)) return;
            OnPropertyChanged(nameof(HasSelectionOrder));
        }
    }

    public bool HasSelectionOrder => SelectionOrder > 0;

    public bool IsCheckVisible => IsSelected || IsHovered;
    public IBrush SelectionBorderBrush => IsSelected ? Brush.Parse("#5864E8") : IsHovered ? Brush.Parse("#5C6D7E") : Brush.Parse("#24303A");
    public Avalonia.Thickness SelectionBorderThickness => IsSelected || IsHovered ? new Avalonia.Thickness(2) : new Avalonia.Thickness(0);

    public void UpdateMedia(MediaFileInfo media)
    {
        _media = media;
        _clipInfo = ClipInfoSidecar.Load(media.Path);
        PreviewImagePath = media.ThumbnailPath;
        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(RelativeDateLabel));
        OnPropertyChanged(nameof(ClipFromLabel));
        OnPropertyChanged(nameof(GameNameLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(CaptureBackendLabel));
        OnPropertyChanged(nameof(HasCaptureBackendLabel));
        OnPropertyChanged(nameof(IsAutoClip));
        OnPropertyChanged(nameof(TileTopLabel));
        OnPropertyChanged(nameof(TileMainLabel));
        OnPropertyChanged(nameof(HasAutoClipIcon));
        OnPropertyChanged(nameof(AutoClipIconGeometry));
        OnPropertyChanged(nameof(AutoClipIconFill));
    }

    private void SetPreviewImage(string path)
    {
        try
        {
            PreviewImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new Bitmap(path)
                : null;
        }
        catch
        {
            PreviewImage = null;
        }
    }
}
