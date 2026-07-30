using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            ShareSize10.IsChecked = true;
            _ = StartShareEncodeAsync(10L * 1024 * 1024);
        };
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void CleanUp()
    {
        _shareCts?.Cancel();
        if (_shareTempPath is { } path) AudioCapturePipeline.TryDelete(path);
        _shareTempPath = null;
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
        if (_shareTempPath is { } previous) AudioCapturePipeline.TryDelete(previous);

        var cts = new CancellationTokenSource();
        _shareCts = cts;
        var tempPath = Path.Combine(Path.GetTempPath(), $"clypdat-share-{Guid.NewGuid():N}.mp4");
        _shareTempPath = tempPath;

        ShareThumbnail.IsVisible = false;
        ShareDurationBadge.IsVisible = false;
        ShareShowInFolderButton.IsEnabled = false;
        ShareResultSizeText.Text = string.Empty;
        ShareProgressBar.IsVisible = true;
        ShareProgressBar.IsIndeterminate = false;
        ShareProgressBar.Value = 0;
        ShareStatusText.Text = "Encoding for Discord...";

        try
        {
            var exportDuration = _viewModel.ExportDuration;
            var progress = new Progress<double>(fraction =>
            {
                if (cts.IsCancellationRequested) return;
                ShareProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
            });

            var result = await MainWindow.RunProcessWithProgressAsync("ffmpeg", _viewModel.BuildShareArguments(tempPath, targetBytes), exportDuration, progress, cts.Token);
            if (result.ExitCode != 0 && !cts.IsCancellationRequested)
            {
                AppLog.Info($"Share: NVENC encode failed, retrying with CPU encoder. ffmpeg said: {result.Error}");
                ShareProgressBar.IsIndeterminate = true;
                ShareStatusText.Text = "Encoding for Discord (CPU encoder)...";
                result = await MainWindow.RunProcessWithProgressAsync("ffmpeg", _viewModel.BuildShareArguments(tempPath, targetBytes, useHardwareEncoder: false), exportDuration, progress, cts.Token);
            }

            if (cts.IsCancellationRequested) return; // Superseded by a later pill click - that call owns cleanup/UI now.

            if (result.ExitCode != 0)
            {
                AudioCapturePipeline.TryDelete(tempPath);
                _shareTempPath = null;
                ShareProgressBar.IsVisible = false;
                ShareStatusText.Text = string.IsNullOrWhiteSpace(result.Error) ? "Encode failed." : result.Error;
                return;
            }

            var spec = _viewModel.ComputeShareEncodeSpec(exportDuration.TotalSeconds, _viewModel.SelectedSourceWidth, _viewModel.SelectedSourceHeight, _viewModel.SelectedSourceFps, targetBytes);
            long actualBytes;
            try { actualBytes = new FileInfo(tempPath).Length; } catch { actualBytes = 0; }
            var actualMb = actualBytes / 1024.0 / 1024.0;
            var targetMb = targetBytes / 1024.0 / 1024.0;

            ShareProgressBar.IsVisible = false;
            ShareStatusText.Text = spec.Downscaled
                ? $"Downscaled to {spec.Height}p{spec.Fps:0} to fit the target size."
                : "Drag this clip into any Discord chat to upload it.";
            ShareResultSizeText.Text = actualMb > targetMb * 1.02
                ? $"{actualMb:0.#} MB (slightly over the {targetMb:0.#} MB target)"
                : $"{actualMb:0.#} MB";
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
            ShareProgressBar.IsVisible = false;
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
        ShareDragActiveOverlay.IsVisible = true;
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
            // DoDragDrop blocks for the whole gesture regardless of where it
            // ends (dropped on a target, released over empty desktop,
            // Escape) - once it returns the clip has left the app's hand
            // either way, so the thumbnail doesn't come back; picking a size
            // again re-encodes and shows a fresh one.
            ShareDragActiveOverlay.IsVisible = false;
            ShareThumbnail.IsVisible = false;
            ShareDurationBadge.IsVisible = false;
            ShareStatusText.Text = "Clip shared.";
        }
#pragma warning restore CS0618
    }

    private void Thumbnail_OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragPressPoint = null;
}
