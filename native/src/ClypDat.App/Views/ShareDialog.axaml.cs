using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public partial class ShareDialog : Window
{
    private MainWindowViewModel _viewModel = null!;
    private CancellationTokenSource? _shareCts;
    private string? _shareTempPath;
    private Point? _dragPressPoint;

    // Parameterless constructor exists only so Avalonia's XAML loader can see
    // this as a valid top-level control (AVLN3001) - never actually used to
    // show a real dialog, the one below always is.
    public ShareDialog() => InitializeComponent();

    public ShareDialog(Window owner, MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        PositionOverOwner(owner);
        Closed += (_, _) => CleanUp();

        // First pill pre-selected and its encode kicked off immediately, so
        // the dialog opens already showing progress/a drop zone instead of a
        // blank "pick a size" placeholder - matches picking a size actually
        // mattering, not gating the whole dialog on one extra click.
        Opened += (_, _) =>
        {
            SweepStaleShareTempFiles();
            ShareSize10.IsChecked = true;
            _ = StartShareEncodeAsync(10L * 1024 * 1024);
        };
    }

    // A hard kill (crash, task manager) never runs the close-time cleanup, so
    // anything left from a previous run would sit in temp forever. These are
    // only ever ours and only ever transient, so clearing whatever is no
    // longer locked costs nothing and keeps them from accumulating.
    private static void SweepStaleShareTempFiles()
    {
        try
        {
            foreach (var stale in Directory.EnumerateDirectories(Path.GetTempPath(), $"{ShareTempFolderPrefix}*"))
            {
                try { Directory.Delete(stale, recursive: true); } catch { /* still in use by this or another instance */ }
            }
            // Flat files from before shares moved into their own folders.
            foreach (var stale in Directory.EnumerateFiles(Path.GetTempPath(), $"{ShareTempFolderPrefix}*.mp4"))
            {
                try { File.Delete(stale); } catch { /* still in use */ }
            }
        }
        catch (Exception error)
        {
            AppLog.Debug($"Share: temp sweep skipped ({error.Message}).");
        }
    }

    // Owner isn't wired through Avalonia's own Owner property for sizing
    // purposes - a modal-feeling popup like this can't be resized/moved by
    // the user while it's up anyway, so position/size are computed once,
    // directly against the owner's REAL win32 rect rather than trusting
    // Avalonia's Bounds/PointToScreen alone. RepositionEditorHoverControls
    // (MainWindow.axaml.cs) documents why: those read fine once a window is
    // already shown (true here - owner always is), but the same class of
    // DIP/physical-pixel mismatch this codebase has hit before is cheap to
    // just avoid by pulling the owner's rect straight from Win32.
    private void PositionOverOwner(Window owner)
    {
        var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            var scaling = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
            Position = new PixelPoint(rect.Left, rect.Top);
            Width = (rect.Right - rect.Left) / scaling;
            Height = (rect.Bottom - rect.Top) / scaling;
            return;
        }

        Position = owner.PointToScreen(new Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    // The file the user drags out keeps a real, readable name ("ClypDat -
    // Fortnite - Jul-30-2026.mp4") because that name is what lands in the
    // Discord message, so a GUID in it would be user-visible. The GUID moves
    // to a wrapping folder instead: it still guarantees uniqueness (two
    // shares of the same clip on the same day would otherwise collide) and
    // still gives the temp sweep a single unmistakable thing to delete.
    private string BuildShareTempPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"{ShareTempFolderPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, BuildShareFileName());
    }

    private const string ShareTempFolderPrefix = "clypdat-share-";

    private string BuildShareFileName()
    {
        var libraryRoot = string.IsNullOrWhiteSpace(_viewModel.Settings.LibraryFolder)
            ? null
            : _viewModel.Settings.LibraryFolder;
        ClipInfo? sidecar = null;
        try
        {
            if (libraryRoot is not null) sidecar = ClipInfoSidecar.Load(libraryRoot, _viewModel.SelectedVideoPath);
        }
        catch
        {
            // A missing/unreadable sidecar just means falling back to the
            // folder name, which ResolveExportGame already handles.
        }

        var game = ClipFileNaming.SanitizeSegment(MainWindow.ResolveExportGame(_viewModel.SelectedVideoPath, sidecar));
        // Same date the editor shows as "Created:" - the clip's own recording
        // date, not whenever Share happened to be clicked.
        var timestamp = _viewModel.SelectedCreatedAtLocal > default(DateTime) ? _viewModel.SelectedCreatedAtLocal : DateTime.Now;
        var date = timestamp.ToString("MMM-dd-yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var stem = string.IsNullOrWhiteSpace(game) ? $"ClypDat - {date}" : $"ClypDat - {game} - {date}";
        return $"{ClipFileNaming.SanitizeSegment(stem)}.mp4";
    }

    private void CleanUp()
    {
        _shareCts?.Cancel();
        _dragCursorWatch?.Stop();
        _dragCursorWatch = null;
        // ffmpeg may still be letting go of the handle when a cancelled
        // encode's process exits, and a drop target (Explorer especially)
        // can hold the file open for a moment after taking it - a single
        // immediate delete loses that race and leaves a multi-hundred-MB
        // temp file behind for good. Retry briefly in the background rather
        // than blocking the close.
        if (_shareTempPath is { } path) _ = DeleteWithRetryAsync(path);
        _shareTempPath = null;
    }

    private static async Task DeleteWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                TryDeleteShareFolder(Path.GetDirectoryName(path));
                return;
            }
            catch
            {
                await Task.Delay(300);
            }
        }
        AppLog.Debug($"Share: could not delete temp file {path} - leaving it for the OS temp sweep.");
    }

    // Only ever removes the GUID-named wrapper this dialog created, never a
    // folder the path merely happens to sit in.
    private static void TryDeleteShareFolder(string? folder)
    {
        if (folder is null) return;
        if (!Path.GetFileName(folder).StartsWith(ShareTempFolderPrefix, StringComparison.Ordinal)) return;
        try { Directory.Delete(folder, recursive: true); } catch { /* something still holds it */ }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void ShareDialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // Header-only drag-move, matching NewClipsOverlay_OnPointerPressed's
    // convention - presses on the card itself are consumed by its own
    // controls before they bubble here, so this only ever sees clicks on the
    // surrounding scrim/header.
    private void Scrim_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.GetPosition(this).Y > 56) return;
        BeginMoveDrag(e);
    }

    private void SizePreset_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio) return;
        var isCustom = ReferenceEquals(radio, ShareSizeCustom);
        ShareCustomSizeBox.IsVisible = isCustom;
        if (isCustom)
        {
            ShareCustomSizeBox.Focus();
            return; // Wait for Enter - free-entry MB value, nothing to encode yet.
        }
        if (radio.Tag is string tagText && double.TryParse(tagText, out var mb))
        {
            _ = StartShareEncodeAsync((long)(mb * 1024 * 1024));
        }
    }

    private void CustomSizeBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (double.TryParse(ShareCustomSizeBox.Text, out var mb) && mb > 0)
        {
            _ = StartShareEncodeAsync((long)(mb * 1024 * 1024));
        }
    }

    // Picking a size re-encodes immediately (no separate "Share" button to
    // press) - the drop zone itself carries the progress/result state so the
    // whole flow stays on one screen instead of a picker step then a result
    // step.
    private async Task StartShareEncodeAsync(long targetBytes)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SelectedVideoPath)) return;

        _shareCts?.Cancel();
        // Superseded by a different size - the old encode's file goes with it.
        if (_shareTempPath is { } previous) _ = DeleteWithRetryAsync(previous);

        var cts = new CancellationTokenSource();
        _shareCts = cts;
        var tempPath = BuildShareTempPath();
        _shareTempPath = tempPath;

        ShareThumbnail.IsVisible = false;
        ShareDurationBadge.IsVisible = false;
        ShareShowInFolderButton.IsEnabled = false;
        ShareResultSizeText.Text = string.Empty;
        ShareProgressPanel.IsVisible = true;
        ShareProgressBar.IsIndeterminate = false;
        ShareProgressBar.Value = 0;
        ShareProgressPercentText.Text = "0%";
        ShareStatusText.Text = targetBytes > 0 ? "Encoding for Discord..." : "Encoding at original quality...";

        try
        {
            var exportDuration = _viewModel.ExportDuration;
            var progress = new Progress<double>(fraction =>
            {
                if (cts.IsCancellationRequested) return;
                ShareProgressBar.IsIndeterminate = false;
                ShareProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
                ShareProgressPercentText.Text = $"{ShareProgressBar.Value:0}%";
            });

            var result = await MainWindow.RunProcessWithProgressAsync("ffmpeg", _viewModel.BuildShareArguments(tempPath, targetBytes), exportDuration, progress, cts.Token);
            if (result.ExitCode != 0 && !cts.IsCancellationRequested)
            {
                AppLog.Info($"Share: NVENC encode failed, retrying with CPU encoder. ffmpeg said: {result.Error}");
                ShareProgressBar.IsIndeterminate = true;
                ShareProgressPercentText.Text = string.Empty;
                ShareStatusText.Text = "Encoding for Discord (CPU encoder)...";
                result = await MainWindow.RunProcessWithProgressAsync("ffmpeg", _viewModel.BuildShareArguments(tempPath, targetBytes, useHardwareEncoder: false), exportDuration, progress, cts.Token);
            }

            if (cts.IsCancellationRequested) return; // Superseded by a later pill click - that call owns cleanup/UI now.

            if (result.ExitCode != 0)
            {
                _ = DeleteWithRetryAsync(tempPath);
                _shareTempPath = null;
                ShareProgressPanel.IsVisible = false;
                ShareStatusText.Text = string.IsNullOrWhiteSpace(result.Error) ? "Encode failed." : result.Error;
                return;
            }

            var spec = _viewModel.ComputeShareEncodeSpec(exportDuration.TotalSeconds, _viewModel.SelectedSourceWidth, _viewModel.SelectedSourceHeight, _viewModel.SelectedSourceFps, targetBytes);
            long actualBytes;
            try { actualBytes = new FileInfo(tempPath).Length; } catch { actualBytes = 0; }
            var actualMb = actualBytes / 1024.0 / 1024.0;
            var targetMb = targetBytes / 1024.0 / 1024.0;

            ShareProgressPanel.IsVisible = false;
            ShareStatusText.Text = "Drag this clip into any Discord chat to upload it";
            // Resolution/fps is always shown, not just when downscaled - what
            // you are about to send is worth knowing either way, and it makes
            // the trade-off a bigger size buys immediately obvious.
            var quality = $"{spec.Height}p{spec.Fps:0}";
            ShareResultSizeText.Text = targetBytes > 0 && actualMb > targetMb * 1.02
                ? $"{actualMb:0.#} MB · {quality} (just over the {targetMb:0.#} MB target)"
                : $"{actualMb:0.#} MB · {quality}";
            ShareShowInFolderButton.IsEnabled = true;

            ShareThumbnail.Source = _viewModel.SelectedThumbnail;
            ShareThumbnail.IsVisible = _viewModel.SelectedThumbnail is not null;
            ShareDurationText.Text = FormatShareDuration(exportDuration);
            ShareDurationBadge.IsVisible = true;
        }
        catch (Exception error)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Error("Share: encode failed", error);
            ShareProgressPanel.IsVisible = false;
            ShareStatusText.Text = "Encode failed.";
        }
    }

    private static string FormatShareDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private void ShowInFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_shareTempPath is { } path) ExplorerService.Open(path, selectFile: true);
    }

    private void Thumbnail_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _dragPressPoint = e.GetPosition(ShareThumbnail);

    private async void Thumbnail_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPressPoint is not { } start || e.GetCurrentPoint(ShareThumbnail).Properties.IsLeftButtonPressed != true) return;
        if (_shareTempPath is not { } tempPath) return;
        var current = e.GetPosition(ShareThumbnail);
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4) return;
        _dragPressPoint = null;

        // Avalonia 11.3 added DataTransfer/DoDragDropAsync as the
        // replacement for DataObject/DoDragDrop, but its write-side API
        // (constructing a file-backed IDataTransferItem) isn't documented
        // anywhere reachable, while this older overload is still shipped,
        // still functional, and is what every Avalonia drag-out sample still
        // shows - suppressed rather than swapped to a barely-verifiable
        // newer path for the same result.
        // DataFormats.Files (not FileNames) expects IEnumerable<IStorageItem>,
        // not raw paths - passing strings there compiles fine (Set takes
        // object) but the native drag backend gets no usable file data out
        // of it, so every drop target shows a permanent no-drop cursor.
        // FileNames is the one that actually wants plain path strings.
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set(DataFormats.FileNames, new[] { tempPath });
        StartDragCursorWatch();
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch (Exception error)
        {
            AppLog.Error("Share: drag-out failed", error);
        }
        finally
        {
            // Everything goes back exactly as it was: the same clip stays
            // draggable for another go. Dropping it back onto ClypDat, onto
            // the desktop, or hitting Escape all end up here, and any of
            // those used to leave the dialog stuck with no thumbnail until
            // the size was changed or the dialog reopened.
            StopDragCursorWatch();
            ShareDragActiveOverlay.IsVisible = false;
        }
#pragma warning restore CS0618
    }

    private void Thumbnail_OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragPressPoint = null;

    // DoDragDrop blocks this method for the whole gesture, and during a drag
    // the app gets no pointer events at all (the OS owns the pointer), so
    // "is the cursor still over ClypDat" can only be answered by polling the
    // cursor against the window rect.
    private DispatcherTimer? _dragCursorWatch;
    private bool _dragLeftApp;

    private void StartDragCursorWatch()
    {
        _dragLeftApp = false;
        ShareDragActiveOverlay.IsVisible = true;
        _dragCursorWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _dragCursorWatch.Tick -= DragCursorWatch_OnTick;
        _dragCursorWatch.Tick += DragCursorWatch_OnTick;
        _dragCursorWatch.Start();
    }

    private void StopDragCursorWatch()
    {
        _dragCursorWatch?.Stop();
        if (_dragCursorWatch is not null) _dragCursorWatch.Tick -= DragCursorWatch_OnTick;
    }

    private void DragCursorWatch_OnTick(object? sender, EventArgs e)
    {
        if (_dragLeftApp) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(handle, out var rect)) return;
        if (cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom) return;

        // One-way: the overlay's whole job is getting ClypDat out of the way
        // while the user aims at another window, and it has done that as soon
        // as the cursor is off the app. Coming back over ClypDat mid-drag
        // (crossing it on the way to something else, or dropping the clip
        // back here) must not slam the panel up again, so the watch latches
        // off and stays off for the rest of this gesture.
        _dragLeftApp = true;
        ShareDragActiveOverlay.IsVisible = false;
        StopDragCursorWatch();
    }
}
