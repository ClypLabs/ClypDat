using Avalonia.Media.Imaging;
using Avalonia.Media;
using ClypDat.App.Services;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class ClipCardViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isHovered;
    private MediaFileInfo _media;
    private readonly string _libraryRoot;
    private string _previewImagePath;
    private Bitmap? _previewImage;
    private ClipInfo? _clipInfo;
    private ClipEditSettings? _clipEdit;
    private bool _isVod;
    private bool _isPreviewVisible;
    private string _repairOverlayText = string.Empty;

    public event EventHandler? PersistentStateChanged;

    public ClipCardViewModel(MediaFileInfo media, string libraryRoot)
        : this(media, libraryRoot, null, null, false)
    {
    }

    internal ClipCardViewModel(CachedClipState state, string libraryRoot)
        : this(state.Media, libraryRoot, state.ClipInfo, state.ClipEdit, true)
    {
    }

    private ClipCardViewModel(MediaFileInfo media, string libraryRoot, ClipInfo? clipInfo, ClipEditSettings? clipEdit, bool hasCachedSidecars)
    {
        _media = media;
        _libraryRoot = libraryRoot;
        _previewImagePath = media.ThumbnailPath;
        _clipInfo = hasCachedSidecars ? clipInfo : ClipInfoSidecar.Load(_libraryRoot, media.Path);
        _clipEdit = hasCachedSidecars ? clipEdit : ClipEditSidecar.Load(_libraryRoot, media.Path);
        _isVod = ComputeIsVod(media, libraryRoot);
        // Thumbnail Bitmap is NOT decoded here - a library can have hundreds
        // of cards and only a screenful are ever actually on screen at once.
        // MainWindow.axaml wires each card's EffectiveViewportChanged to
        // SetPreviewVisible, which does the real decode/dispose lazily as
        // cards scroll in and out of the ScrollViewer's viewport.
    }

    // Authoritative via path - a clip was already sorted into Clips/ or
    // VODs/ at save time by LibraryLayout.VideoDirectory, so this doesn't
    // need to wait on ffprobe duration hydration to be correct. Duration is
    // just a secondary fallback for the rare case a long file ended up
    // outside VODs/ some other way.
    private static bool ComputeIsVod(MediaFileInfo media, string libraryRoot)
    {
        if (media.Duration.TotalSeconds > LibraryLayout.ClipMaximumDurationSeconds) return true;
        if (string.IsNullOrWhiteSpace(libraryRoot)) return false;
        var relative = System.IO.Path.GetRelativePath(LibraryLayout.VodsRoot(libraryRoot), media.Path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative);
    }

    public MediaFileInfo Media => _media;
    public string Name => Media.Name;
    public string Path => Media.Path;

    // Set while the background sweep has this clip queued for, or is actively
    // performing, a parameter-set repair. The card dims its thumbnail and shows
    // this text over it - the clip is unwatchable until the repair lands, so
    // saying so on the tile itself beats a progress bar elsewhere on the page.
    public string RepairOverlayText
    {
        get => _repairOverlayText;
        set
        {
            if (!SetProperty(ref _repairOverlayText, value)) return;
            OnPropertyChanged(nameof(IsRepairOverlayVisible));
        }
    }

    public bool IsRepairOverlayVisible => !string.IsNullOrEmpty(RepairOverlayText);
    public DateTimeOffset CreatedAt => IsSteelSeriesImport && _clipInfo?.CapturedAt is { } capturedAt ? capturedAt : Media.CreatedAt;
    public TimeSpan Duration => Media.Duration;
    public long SizeBytes => Media.SizeBytes;
    public DateTime LastWriteTimeUtc => Media.LastWriteTimeUtc;

    // False while HydrateLibraryClipsAsync hasn't reached this card yet (or
    // its probe genuinely failed) - same stub-detection check OpenClipAsync
    // already used to decide whether to re-probe on open. Lets the click
    // handler tell the user to wait instead of opening an editor with no
    // duration/tracks to work with.
    public bool IsHydrated => Duration > TimeSpan.Zero && Media.Tracks.Count > 0;
    public string DateLabel => CreatedAt.ToString("MMM d, yyyy h:mm tt");

    // Per-card date header shown above the thumbnail (replaces the old
    // shared per-day group header's Label - same format, "SAT, JUL 11").
    public string DateHeaderLabel => CreatedAt.ToLocalTime().ToString("ddd, MMM d").ToUpperInvariant();

    // Name is the filename ClipFileNaming.BuildFileName produced - the game/
    // auto-clip label plus a " yyyy-MM-dd HH-mm-ss" timestamp appended for
    // uniqueness on disk (e.g. "Marvel Rivals 2026-07-11 22-16-11"). Strip that
    // suffix back off for display; there's no separately stored game field.
    public string GameNameLabel => NormalizeGameDisplayName(_clipInfo?.FileTitle ?? ClipFileNaming.StripTimestampSuffix(Name));

    public string ClipFromLabel => _clipInfo?.IsExport == true
        ? $"Exported clip from: {CreatedAt:MMM d, yyyy}"
        : $"Clip from {CreatedAt:MMM d, yyyy}";

    // User-set label shown instead of ClipFromLabel on a non-auto-clip's
    // tile - kept separate from GameNameLabel/FileTitle so renaming never
    // overwrites the game association or a Medal import's original title.
    public string? CustomTitle => _clipInfo?.CustomTitle;

    // For a CS2 auto-clip, GameNameLabel is really "<event> - <map>" (e.g.
    // "3K - Mirage") since that's what the auto-clip title became when it was
    // used to build the filename - swap the tile around for those: lead with
    // the event/map (the interesting part) and show the actual game name
    // (from the sidecar, not parseable out of the filename) as the small label
    // above it instead of "Clip from <date>".
    public bool IsAutoClip => !string.IsNullOrWhiteSpace(_clipInfo?.AutoClipEventType);
    public bool IsVod => _isVod;
    public bool IsMedalImport => !string.IsNullOrWhiteSpace(_clipInfo?.MedalImportKey);
    public bool IsSteelSeriesImport => !string.IsNullOrWhiteSpace(_clipInfo?.SteelSeriesImportKey);
    public bool IsExternalImport => IsMedalImport || IsSteelSeriesImport;
    public bool IsManualClip => !IsAutoClip && !IsVod && !IsExternalImport;
    public string TileTopLabel => IsAutoClip || IsExternalImport
        ? NormalizeGameDisplayName(_clipInfo!.GameDisplayName ?? GameNameLabel)
        : GameNameLabel;
    public string TileMainLabel => IsAutoClip
        ? GameNameLabel
        : IsSteelSeriesImport
            ? (_clipInfo?.FileTitle ?? ClipFromLabel)
            : IsExternalImport
                ? GameNameLabel
                : (CustomTitle ?? ClipFromLabel);
    public string AutoClipEventTypeLabel => _clipInfo?.AutoClipEventType ?? string.Empty;

    // Matches Cs2GsiListener's label format: "Kill", "2K".."4K", "Ace", each
    // optionally prefixed "Headshot ". Death/Assist have no kill count.
    public int AutoClipKillCount => _clipInfo?.AutoClipEventType switch
    {
        null => 0,
        var type when type.EndsWith("Ace", StringComparison.OrdinalIgnoreCase) => 5,
        var type when type.EndsWith("4K", StringComparison.OrdinalIgnoreCase) => 4,
        var type when type.EndsWith("3K", StringComparison.OrdinalIgnoreCase) => 3,
        var type when type.EndsWith("2K", StringComparison.OrdinalIgnoreCase) => 2,
        var type when type.EndsWith("Kill", StringComparison.OrdinalIgnoreCase) => 1,
        var type when type.Equals("Headshot", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 0
    };

    public bool HasAutoClipKillCount => AutoClipKillCount > 0;

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
        get => FormatRelativeDate(CreatedAt, DateTimeOffset.Now);
    }

    internal static string FormatRelativeDate(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var age = now - createdAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min{((int)age.TotalMinutes == 1 ? "" : "s")} ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
        return createdAt.ToString("MMM d, yyyy");
    }

    internal void RefreshRelativeDateLabel() => OnPropertyChanged(nameof(RelativeDateLabel));
    // If the clip has saved trim edits, show the trimmed length (what export
    // would actually produce) instead of the raw file's duration - with a
    // pencil indicator next to it so it's clear the number isn't the file's
    // full length.
    public TimeSpan TrimmedDuration
    {
        get
        {
            if (_clipEdit is null || Duration <= TimeSpan.Zero) return Duration;
            var start = TimeSpan.FromSeconds(Math.Clamp(_clipEdit.TrimStartSeconds, 0, Duration.TotalSeconds));
            var end = TimeSpan.FromSeconds(Math.Clamp(_clipEdit.TrimEndSeconds, 0, Duration.TotalSeconds));
            if (end <= TimeSpan.Zero || end < start) end = Duration;
            return end - start;
        }
    }

    public bool HasTrimEdit => _clipEdit is not null && Duration - TrimmedDuration > TimeSpan.FromMilliseconds(50);
    public string DurationLabel => TrimmedDuration > TimeSpan.Zero ? TrimmedDuration.ToString("m\\:ss") : "0:00";
    public string GameLabel => "VIDEO";
    public string CaptureBackendLabel => IsMedalImport
        ? "Imported from Medal"
        : IsSteelSeriesImport
            ? "Imported from SteelSeries Moments"
        : "Captured with: ClypDat";
    public bool HasCaptureBackendLabel => !string.IsNullOrWhiteSpace(CaptureBackendLabel);

    // The per-game filter's grouping key - reuses TileTopLabel since that
    // already resolves to the real game name for both auto-clips (sidecar's
    // GameDisplayName) and everything else (filename-parsed), for both
    // ClypDat-recorded and Medal-imported clips.
    public string GameFilterKey => NormalizeGameDisplayName(_clipInfo?.GameDisplayName ?? TileTopLabel);

    internal static string NormalizeGameDisplayName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.EndsWith(" (Trimmed)", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^" (Trimmed)".Length].TrimEnd();
        }

        return normalized.ToUpperInvariant() switch
        {
            "DESKTOP" or "DESKTOPCAPTURE" => "Desktop Capture",
            "FORTNITECLIENT-WIN64-SHIPPING" or "FORTNITECLIENT-WIN64-SHIPPING.EXE" => "Fortnite",
            "ROBLOXPLAYERBETA" or "ROBLOXPLAYERBETA.EXE" or "ROBLOXPLAYERLAUNCHER" or "ROBLOXPLAYERLAUNCHER.EXE" => "Roblox",
            "VALORANT" or "VALORANT-WIN64-SHIPPING" or "VALORANT-WIN64-SHIPPING.EXE" => "Valorant",
            "LEAGUECLIENT" or "LEAGUECLIENT.EXE" or "LEAGUECLIENTUX" or "LEAGUECLIENTUX.EXE" or "LEAGUECLIENTUXRELEASE" or "LEAGUECLIENTUXRELEASE.EXE" => "League of Legends",
            _ => normalized
        };
    }

    private string _setGameActionLabel = "Change game";

    // Mirrors RenameActionLabel for the "which game is this clip" action.
    public string SetGameActionLabel
    {
        get => _setGameActionLabel;
        set => SetProperty(ref _setGameActionLabel, value);
    }

    // Any clip can be filed under a different game from its context menu.
    // This is especially useful for imports and captures made while game
    // detection was unavailable or selected the wrong process.
    public bool CanChangeGame => true;

    private string _renameActionLabel = "Rename";

    // What this card's context-menu rename entry reads - "Rename All" once
    // it's part of a multi-selection, since the action then applies to every
    // selected clip. Set by MainWindowViewModel whenever the selection
    // changes; the card itself has no view of the selection as a whole.
    public string RenameActionLabel
    {
        get => _renameActionLabel;
        set => SetProperty(ref _renameActionLabel, value);
    }

    private bool _isMatchedByGameFilter = true;

    // Drives this card's own visibility when a game filter is active -
    // toggled by MainWindowViewModel's game-filter checklist, not by
    // anything local.
    public bool IsMatchedByGameFilter
    {
        get => _isMatchedByGameFilter;
        set
        {
            var wasVisible = IsVisibleInLibrary;
            if (!SetProperty(ref _isMatchedByGameFilter, value)) return;
            if (wasVisible != IsVisibleInLibrary) OnPropertyChanged(nameof(IsVisibleInLibrary));
        }
    }

    private bool _isMatchedByClipTypeFilter = true;

    // Same shape as IsMatchedByGameFilter but for the clip-type checklist -
    // toggled by MainWindowViewModel, kept as a separate flag so the two
    // filter groups can be combined with AND (IsVisibleInLibrary) while each
    // stays independently OR'd within its own group.
    public bool IsMatchedByClipTypeFilter
    {
        get => _isMatchedByClipTypeFilter;
        set
        {
            var wasVisible = IsVisibleInLibrary;
            if (!SetProperty(ref _isMatchedByClipTypeFilter, value)) return;
            if (wasVisible != IsVisibleInLibrary) OnPropertyChanged(nameof(IsVisibleInLibrary));
        }
    }

    private bool _isMatchedBySearch = true;

    // Same shape again, for the library search box - toggled by
    // MainWindowViewModel against the clip's title/game text.
    public bool IsMatchedBySearch
    {
        get => _isMatchedBySearch;
        set
        {
            var wasVisible = IsVisibleInLibrary;
            if (!SetProperty(ref _isMatchedBySearch, value)) return;
            if (wasVisible != IsVisibleInLibrary) OnPropertyChanged(nameof(IsVisibleInLibrary));
        }
    }

    // What the card's own Border.IsVisible binds to - the game filter,
    // clip-type filter, and search box all have to match (AND across
    // groups; each checklist group's own set membership is an OR).
    public bool IsVisibleInLibrary => IsMatchedByGameFilter && IsMatchedByClipTypeFilter && IsMatchedBySearch;

    public string PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (!SetProperty(ref _previewImagePath, value)) return;
            if (_isPreviewVisible) SetPreviewImage(value);
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    internal (TimeSpan Start, TimeSpan Duration) HoverPreviewRange
    {
        get
        {
            var total = Duration > TimeSpan.Zero ? Duration : TimeSpan.Zero;
            var start = TimeSpan.FromSeconds(Math.Clamp(_clipEdit?.TrimStartSeconds ?? 0, 0, total.TotalSeconds));
            var end = TimeSpan.FromSeconds(Math.Clamp(_clipEdit?.TrimEndSeconds ?? 0, 0, total.TotalSeconds));
            if (end <= start) end = total;
            return (start, end - start);
        }
    }

    // Called by MainWindow's per-card EffectiveViewportChanged handler as
    // cards cross the ScrollViewer's viewport - decodes the thumbnail on
    // entry and disposes/releases it on exit, so a large library never has
    // more than a screenful of decoded bitmaps live at once.
    public void SetPreviewVisible(bool visible)
    {
        if (_isPreviewVisible == visible) return;
        _isPreviewVisible = visible;

        if (visible)
        {
            SetPreviewImage(_previewImagePath);
        }
        else
        {
            var old = _previewImage;
            PreviewImage = null;
            // Deferred, not immediate - see DeferredBitmapDisposal. Opening a
            // clip collapses the library scroller and runs this for every
            // realized card in a single turn, while the compositor is still
            // drawing the frame those bitmaps belong to.
            DeferredBitmapDisposal.Release(old);
        }
    }

    // Called after the thumbnail file at PreviewImagePath has been
    // regenerated IN PLACE (same path, new bytes - e.g. moving TrimStart in
    // the editor) - PreviewImagePath's own setter no-ops when the string
    // itself hasn't changed, so this bypasses it to force a redecode of
    // whatever's actually on disk now.
    public void RefreshPreviewImage()
    {
        if (_isPreviewVisible) SetPreviewImage(_previewImagePath);
    }

    // The editor just wrote this clip's edit sidecar. _clipEdit was otherwise
    // only ever loaded in the constructor and in UpdateMedia, so until the next
    // full library refresh the card kept answering from the pre-edit values -
    // which is why a hover preview still started at the OLD trim point despite
    // HoverPreviewRange reading TrimStartSeconds correctly all along, and why
    // the duration label kept showing the untrimmed length.
    //
    // Takes the settings object the caller just saved rather than re-reading
    // the sidecar: this runs on the UI thread from every trim/volume commit,
    // and the value is already in hand.
    public void ApplyClipEdit(ClipEditSettings edit)
    {
        _clipEdit = edit;
        OnPropertyChanged(nameof(TrimmedDuration));
        OnPropertyChanged(nameof(HasTrimEdit));
        OnPropertyChanged(nameof(DurationLabel));
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

    private bool _isDaySelected;

    // Set by MainWindowViewModel.UpdateDaySelectionStates() - true only when
    // every clip sharing this card's date is currently selected. Drives the
    // checked state of the per-card date-header checkbox, which selects the
    // whole day at once (ToggleDaySelection), not just this one card.
    public bool IsDaySelected
    {
        get => _isDaySelected;
        set => SetProperty(ref _isDaySelected, value);
    }

    private bool _isFirstOfDate;

    // Set by MainWindowViewModel - true only for the first (topmost, since
    // AllClips is sorted newest-first) clip of each distinct date. The
    // per-card date header only renders on that one card, not repeated on
    // every clip sharing the date - matches the old shared per-day group
    // header's single-header-per-day behavior, just relocated onto whichever
    // card happens to be first instead of a separate row of its own.
    public bool IsFirstOfDate
    {
        get => _isFirstOfDate;
        set
        {
            if (!SetProperty(ref _isFirstOfDate, value)) return;
            OnPropertyChanged(nameof(DateHeaderOpacity));
        }
    }

    // The date row's own Opacity/IsHitTestVisible (not IsVisible) drives
    // whether it's shown - IsVisible="False" would collapse the row's
    // layout space entirely, so a non-first-of-date card's thumbnail would
    // start higher than its siblings sharing the same WrapPanel row (an
    // uneven, misaligned-looking grid). Keeping the row's height reserved
    // on every card, just visually empty and non-interactive on the ones
    // that aren't first, keeps every card in a row the same height.
    public double DateHeaderOpacity => IsFirstOfDate ? 1.0 : 0.0;

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

    internal CachedClipState ToCachedState() => new(_media, _clipInfo, _clipEdit);

    public void UpdateMedia(MediaFileInfo media, bool reloadSidecars = true)
    {
        _media = media;
        if (reloadSidecars)
        {
            _clipInfo = ClipInfoSidecar.Load(_libraryRoot, media.Path);
            _clipEdit = ClipEditSidecar.Load(_libraryRoot, media.Path);
        }
        _isVod = ComputeIsVod(media, _libraryRoot);
        PreviewImagePath = media.ThumbnailPath;
        OnPropertyChanged(nameof(Media));
        // Path and GameFilterKey both move when a clip is renamed into another
        // game's folder - without these the card kept filtering (and being
        // looked up) under the game it used to belong to.
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(GameFilterKey));
        OnPropertyChanged(nameof(IsVod));
        OnPropertyChanged(nameof(IsMedalImport));
        OnPropertyChanged(nameof(IsSteelSeriesImport));
        OnPropertyChanged(nameof(IsExternalImport));
        OnPropertyChanged(nameof(CanChangeGame));
        OnPropertyChanged(nameof(IsManualClip));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(IsHydrated));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(LastWriteTimeUtc));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(DateHeaderLabel));
        OnPropertyChanged(nameof(RelativeDateLabel));
        OnPropertyChanged(nameof(ClipFromLabel));
        OnPropertyChanged(nameof(GameNameLabel));
        OnPropertyChanged(nameof(TrimmedDuration));
        OnPropertyChanged(nameof(HasTrimEdit));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(CaptureBackendLabel));
        OnPropertyChanged(nameof(HasCaptureBackendLabel));
        OnPropertyChanged(nameof(IsAutoClip));
        OnPropertyChanged(nameof(TileTopLabel));
        OnPropertyChanged(nameof(TileMainLabel));
        OnPropertyChanged(nameof(HasAutoClipIcon));
        OnPropertyChanged(nameof(AutoClipIconGeometry));
        OnPropertyChanged(nameof(AutoClipIconFill));
        OnPropertyChanged(nameof(AutoClipEventTypeLabel));
        OnPropertyChanged(nameof(AutoClipKillCount));
        OnPropertyChanged(nameof(HasAutoClipKillCount));
        OnPropertyChanged(nameof(GameFilterKey));
        PersistentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Pixel width to decode card thumbnails at, kept in step with CardWidth by
    // MainWindowViewModel. Static because every card in the library is the same
    // width - there is no per-card answer to give.
    //
    // Thumbnails are written 960px wide (MediaProbeService's "scale=960:-2"),
    // and decoding one at full size costs ~2MB of BGRA per visible card for a
    // card that is usually 220-500px across. DecodeToWidth lets Skia downscale
    // during decode, so both the pixels and the work scale with what is
    // actually shown.
    private const int MinimumPreviewDecodeWidth = 160;
    private static int _previewDecodeWidth = 480;

    public static void SetPreviewDecodeWidth(double cardWidth, double renderScaling)
    {
        var scale = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        var width = cardWidth * scale;
        if (!double.IsFinite(width) || width < MinimumPreviewDecodeWidth) width = MinimumPreviewDecodeWidth;
        // Never decode LARGER than the source - DecodeToWidth would upscale,
        // spending memory to add no detail.
        _previewDecodeWidth = Math.Min(ThumbnailSourceWidth, (int)Math.Round(width));
    }

    // Matches MediaProbeService's thumbnail scale.
    private const int ThumbnailSourceWidth = 960;

    private void SetPreviewImage(string path)
    {
        var old = _previewImage;
        try
        {
            PreviewImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? DecodePreview(path)
                : null;
        }
        catch
        {
            PreviewImage = null;
        }
        finally
        {
            if (old is not null && old != _previewImage) DeferredBitmapDisposal.Release(old);
        }
    }

    private static Bitmap DecodePreview(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            return Bitmap.DecodeToWidth(stream, _previewDecodeWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            // Some encoders produce headers DecodeToWidth cannot size up front.
            // A full decode still shows the card rather than leaving it blank.
            stream.Position = 0;
            return new Bitmap(stream);
        }
    }
}
