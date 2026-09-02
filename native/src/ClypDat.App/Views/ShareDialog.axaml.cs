using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public partial class ShareDialog : Window
{
    private ShareBackdropWindow? _backdrop;
    private ShareDragOverlayWindow? _dragOverlay;
    private Window? _coveredWindow;
    private MainWindowViewModel _viewModel = null!;
    private CancellationTokenSource? _shareCts;
    // Drag target may be user source or our encoded temp output. Only temp is
    // owned by this dialog and safe to delete.
    private string? _sharePath;
    private string? _shareTempPath;
    private Point? _dragPressPoint;
    private PointerPressedEventArgs? _dragPressEvent;

    // Below 90% of the cap is worth spending a retry to close, above it the
    // gain isn't worth another full encode.
    private const double ShareUndershootRetryThreshold = 0.9;
    // Upper bound on how far a single undershoot retry can scale the bitrate
    // up, so a near-static/black clip can't demand an absurd bitrate off one
    // lucky undershoot.
    private const double ShareBitrateScaleCeiling = 3.0;

    // Parameterless constructor exists only so Avalonia's XAML loader can see
    // this as a valid top-level control (AVLN3001) - never actually used to
    // show a real dialog, the one below always is.
    public ShareDialog() => InitializeComponent();

    public ShareDialog(Window owner, MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        _coveredWindow = owner;
        DataContext = viewModel;
        Closed += (_, _) =>
        {
            CloseDragOverlay();
            _backdrop?.Close();
            _backdrop = null;
            CleanUp();
        };
        Opened += (_, _) => OverlayTransparencyDiagnostics.Log(this, "share-dialog");
        // The empty dashed box is the drop zone, so it needs the clip's shape
        // before the first encode has produced anything to put in it.
        Opened += (_, _) => SizeSharePreviewBox();

        // Deliberately does NOT start encoding on open. Encoding is expensive
        // GPU work, and this app is usually running with the replay buffer
        // live while a game is in the foreground - kicking off a maximum-
        // effort NVENC job just because a dialog appeared spikes the GPU for
        // something the user has not asked for yet, and competes with the
        // capture encoder that is protecting their gameplay.
        Opened += (_, _) => SweepStaleShareTempFiles();
    }

    public async Task ShowWithBackdropAsync(Window owner)
    {
        var backdrop = new ShareBackdropWindow(owner);
        _backdrop = backdrop;
        EventHandler dismiss = (_, _) => Close();
        backdrop.DismissRequested += dismiss;
        backdrop.Show(owner);
        try
        {
            // ShowDialog disables its owner, which blocks the backdrop from
            // receiving the outside click. Keep the ownership chain for
            // z-order, then await this owned window's normal close instead.
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? closedHandler = null;
            closedHandler = (_, _) => closed.TrySetResult();
            Closed += closedHandler;
            try
            {
                Show(backdrop);
                await closed.Task;
            }
            finally
            {
                Closed -= closedHandler;
            }
        }
        finally
        {
            backdrop.DismissRequested -= dismiss;
            backdrop.Close();
            if (ReferenceEquals(_backdrop, backdrop)) _backdrop = null;
        }
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
        _sharePath = null;
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

    // Unlike NewClipsOverlay_OnPointerPressed (which lives INSIDE MainWindow,
    // so BeginMoveDrag there deliberately keeps the real app window draggable
    // through its scrim), ShareDialog is its own separate top-level Window -
    // calling BeginMoveDrag here would drag the popup itself off of its
    // owner instead. Presses on the card are consumed by its own controls
    // before they bubble here, so this only ever sees clicks on the
    // surrounding scrim, which should just do nothing.
    private void Scrim_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
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
            _lastTargetBytes = MegabytesToTargetBytes(mb);
            if (_lastTargetBytes == 0)
            {
                // Set target first, so AV1 change cannot restart encoding.
                ShareAv1Toggle.IsChecked = false;
                PrepareOriginalShare();
                return;
            }
            _ = StartShareEncodeAsync(_lastTargetBytes);
        }
    }

    private void CustomSizeBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (double.TryParse(ShareCustomSizeBox.Text, out var mb) && mb > 0)
        {
            _lastTargetBytes = MegabytesToTargetBytes(mb);
            _ = StartShareEncodeAsync(_lastTargetBytes);
        }
    }

    // Decimal MB, not MiB. "10 MB" has to come out under Discord's free-tier
    // cap whichever way Discord counts it, and 10,000,000 bytes is under
    // both 10 MB and 10 MiB. Erring 5% small here is invisible; erring large
    // makes the file unsendable, which is the entire point of the preset.
    private static long MegabytesToTargetBytes(double megabytes) => (long)(megabytes * 1_000_000);

    private long _lastTargetBytes;

    private void PrepareOriginalShare()
    {
        var sourcePath = _viewModel.SelectedVideoPath;
        _shareCts?.Cancel();
        _shareCts = null;
        if (_shareTempPath is { } previous) _ = DeleteWithRetryAsync(previous);
        _shareTempPath = null;
        _sharePath = null;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            ShareStatusText.Text = "Original clip is unavailable.";
            ShareShowInFolderButton.IsEnabled = false;
            return;
        }

        _sharePath = sourcePath;

        try
        {
            var sourceBytes = new FileInfo(sourcePath).Length;
            var quality = _viewModel.SelectedSourceHeight > 0
                ? $"{_viewModel.SelectedSourceHeight}p{_viewModel.SelectedSourceFps:0}"
                : "Original";

            ShareProgressPanel.IsVisible = false;
            ShareStatusText.Text = "Drag original clip into any chat to upload it";
            ShareResultSizeText.Text = $"{sourceBytes / 1_000_000.0:0.#} MB · {quality}";
            ShareShowInFolderButton.Content = "Show in folder";
            ShareShowInFolderButton.IsEnabled = true;
            // Original sends source bytes. Do not show pending crop/trim edits.
            ShareThumbnail.Source = _viewModel.SelectedThumbnail;
            ShareThumbnail.IsVisible = ShareThumbnail.Source is not null;
            SizeSharePreviewBox(useSourceDimensions: true);
            ShareDurationText.Text = FormatShareDuration(_viewModel.Duration);
            ShareDurationBadge.IsVisible = true;
        }
        catch (Exception error)
        {
            _sharePath = null;
            ShareShowInFolderButton.IsEnabled = false;
            ShareStatusText.Text = "Original clip is unavailable.";
            AppLog.Error("Share: could not prepare original clip", error);
        }
    }

    private void Av1Toggle_OnChanged(object? sender, RoutedEventArgs e)
    {
        // Codec change invalidates whatever is already encoded, so redo it at
        // the size that is currently selected.
        if (!IsLoaded || _lastTargetBytes <= 0) return;
        _ = StartShareEncodeAsync(_lastTargetBytes);
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
        _sharePath = null;

        var cts = new CancellationTokenSource();
        _shareCts = cts;
        var tempPath = BuildShareTempPath();
        _shareTempPath = tempPath;

        ShareThumbnail.IsVisible = false;
        ShareDurationBadge.IsVisible = false;
        // The box carries the aspect even while empty, so switching size preset
        // does not flash the previous clip's shape back.
        SizeSharePreviewBox();
        ShareShowInFolderButton.Content = "Cancel";
        ShareShowInFolderButton.IsEnabled = true;
        ShareResultSizeText.Text = string.Empty;
        ShareProgressPanel.IsVisible = true;
        ShareProgressBar.IsIndeterminate = false;
        ShareProgressBar.Value = 0;
        ShareProgressPercentText.Text = "0%";
        ShareProgressEtaText.IsVisible = false;
        ShareStatusText.Text = targetBytes > 0 ? "Encoding..." : "Encoding at original quality...";

        try
        {
            var exportDuration = _viewModel.ExportDuration;
            // Restarted per attempt (CPU fallback, size retry) - each one is a
            // fresh encode at a different speed, so carrying the previous
            // one's elapsed time over would make the estimate nonsense.
            var encodeClock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(fraction =>
            {
                if (cts.IsCancellationRequested) return;
                ShareProgressBar.IsIndeterminate = false;
                ShareProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
                ShareProgressPercentText.Text = $"{ShareProgressBar.Value:0}%";
                // Below a few percent one early sample extrapolates wildly,
                // so hold off rather than show a number that then collapses.
                if (fraction > 0.03)
                {
                    var remaining = TimeSpan.FromMilliseconds(encodeClock.ElapsedMilliseconds * (1 - fraction) / fraction);
                    ShareProgressEtaText.Text = $"About {MainWindow.FormatEta(remaining)} left";
                    ShareProgressEtaText.IsVisible = true;
                }
            });

            var useAv1 = ShareAv1Toggle.IsChecked == true;
            var tier = MainWindowViewModel.ShareEncoderTier.Nvenc;
            var useAdvancedNvenc = true;
            var bitrateScale = 1.0;
            long actualBytes = 0;
            MainWindow.ProcessResult result;

            async Task<MainWindow.ProcessResult> TryTierAsync() =>
                await MainWindow.RunProcessWithProgressAsync("ffmpeg", _viewModel.BuildShareArguments(tempPath, targetBytes, tier, useAv1, bitrateScale, useAdvancedNvenc), exportDuration, progress, cts.Token, background: true);

            // Walks NVENC -> AMD AMF -> Intel QSV -> CPU from wherever `tier`
            // currently sits, same "try, fall through if this vendor doesn't
            // answer" shape as the native capture engine's EncoderCandidates
            // (NativeReplayBuffer.cs). Only ever moves forward - a tier that
            // failed once is assumed unusable for the rest of this dialog's
            // session, so a later size/codec retry doesn't re-probe hardware
            // that already said no.
            async Task<MainWindow.ProcessResult> WalkEncoderLadderAsync()
            {
                var r = await TryTierAsync();

                // Pre-Turing cards reject temporal-aq/b_ref_mode outright, so
                // drop just those before writing NVENC off entirely.
                if (r.ExitCode != 0 && tier == MainWindowViewModel.ShareEncoderTier.Nvenc && useAdvancedNvenc && !cts.IsCancellationRequested)
                {
                    AppLog.Info($"Share: NVENC rejected the advanced quality options, retrying without them. ffmpeg said: {r.Error}");
                    useAdvancedNvenc = false;
                    encodeClock.Restart();
                    r = await TryTierAsync();
                }

                while (r.ExitCode != 0 && tier != MainWindowViewModel.ShareEncoderTier.Cpu && !cts.IsCancellationRequested)
                {
                    var previousTier = tier;
                    tier = tier switch
                    {
                        MainWindowViewModel.ShareEncoderTier.Nvenc => MainWindowViewModel.ShareEncoderTier.Amf,
                        MainWindowViewModel.ShareEncoderTier.Amf => MainWindowViewModel.ShareEncoderTier.Qsv,
                        _ => MainWindowViewModel.ShareEncoderTier.Cpu
                    };
                    AppLog.Info($"Share: {previousTier} encode failed, trying {tier}. ffmpeg said: {r.Error}");
                    if (tier == MainWindowViewModel.ShareEncoderTier.Cpu)
                    {
                        ShareProgressBar.IsIndeterminate = true;
                        ShareProgressPercentText.Text = string.Empty;
                        ShareStatusText.Text = "Encoding (CPU encoder)...";
                        ShareProgressEtaText.IsVisible = false;
                    }
                    encodeClock.Restart();
                    r = await TryTierAsync();
                }

                return r;
            }

            // A size cap that is only usually honoured is worthless - a file
            // one byte over Discord's free-tier limit simply will not send.
            // So the finished file is measured, and if it came out over, the
            // encode is repeated with the budget scaled down by however much
            // it missed by. Two retries is enough in practice; rate control
            // overshoots by a few percent, not by multiples.
            for (var attempt = 0; ; attempt++)
            {
                result = await WalkEncoderLadderAsync();

                // AV1 needs an RTX 40-series+ card (or an AV1-capable
                // AMD/Intel encoder); nothing in the ladder above could open
                // it. Falling back to H.264 hardware, rather than a software
                // AV1 encoder, keeps this on the fast path the rest of the
                // ladder assumes - libaom-av1/libsvtav1 are minutes rather
                // than seconds for a clip this size.
                if (result.ExitCode != 0 && useAv1 && !cts.IsCancellationRequested)
                {
                    AppLog.Info("Share: AV1 unavailable on every encoder tried, falling back to H.264.");
                    useAv1 = false;
                    tier = MainWindowViewModel.ShareEncoderTier.Nvenc;
                    useAdvancedNvenc = true;
                    result = await WalkEncoderLadderAsync();
                }

                if (cts.IsCancellationRequested) return; // Superseded by a later pill click - that call owns cleanup/UI now.
                if (result.ExitCode != 0) break;

                try { actualBytes = new FileInfo(tempPath).Length; } catch { actualBytes = 0; }
                if (targetBytes <= 0 || attempt >= 2) break;

                string statusText;
                if (actualBytes > targetBytes)
                {
                    // Aim for 95% of the cap rather than exactly the cap, so
                    // the next attempt has somewhere to land instead of
                    // grazing it again.
                    bitrateScale *= targetBytes * 0.95 / (double)actualBytes;
                    AppLog.Info($"Share: {actualBytes / 1024.0 / 1024.0:0.##} MB overshot the {targetBytes / 1024.0 / 1024.0:0.##} MB cap - re-encoding at {bitrateScale:P0} of the original bitrate.");
                    statusText = "Tightening to fit the size limit...";
                }
                else if (actualBytes < targetBytes * ShareUndershootRetryThreshold)
                {
                    // VBR rate control is a target, not a floor - easy-to-
                    // compress content can legitimately land well under the
                    // cap. Rather than leaving that headroom unused, scale
                    // back UP toward the cap the same way the overshoot case
                    // scales down. Clamped so a near-static/black clip can't
                    // demand an absurd bitrate off one lucky undershoot.
                    bitrateScale = Math.Min(bitrateScale * targetBytes * 0.95 / (double)actualBytes, ShareBitrateScaleCeiling);
                    AppLog.Info($"Share: {actualBytes / 1024.0 / 1024.0:0.##} MB undershot the {targetBytes / 1024.0 / 1024.0:0.##} MB cap - re-encoding at {bitrateScale:P0} of the original bitrate to use the headroom.");
                    statusText = "Using the extra headroom for higher quality...";
                }
                else
                {
                    break;
                }

                ShareProgressBar.Value = 0;
                ShareProgressPercentText.Text = "0%";
                ShareProgressEtaText.IsVisible = false;
                encodeClock.Restart();
                ShareStatusText.Text = statusText;
            }

            // A newer preset or Original selection now owns dialog state.
            if (cts.IsCancellationRequested || !ReferenceEquals(_shareCts, cts)) return;

            if (result.ExitCode != 0)
            {
                _ = DeleteWithRetryAsync(tempPath);
                _shareTempPath = null;
                ShareProgressPanel.IsVisible = false;
                if (ReferenceEquals(_shareCts, cts)) _shareCts = null;
                ShareShowInFolderButton.Content = "Show in folder";
                ShareShowInFolderButton.IsEnabled = false;
                ShareStatusText.Text = string.IsNullOrWhiteSpace(result.Error) ? "Encode failed." : result.Error;
                return;
            }

            // Cropped dimensions, matching what BuildShareArguments budgeted and
            // encoded for - handing this the source size labels a cropped clip
            // with a resolution it was never encoded at.
            var crop = _viewModel.ActiveCropRect;
            var spec = _viewModel.ComputeShareEncodeSpec(exportDuration.TotalSeconds, crop?.Width ?? _viewModel.SelectedSourceWidth, crop?.Height ?? _viewModel.SelectedSourceHeight, _viewModel.SelectedSourceFps, targetBytes, useAv1);
            var actualMb = actualBytes / 1_000_000.0;

            ShareProgressPanel.IsVisible = false;
            ShareStatusText.Text = "Drag this clip into any chat to upload it";
            // Resolution/fps is always shown, not just when downscaled - what
            // you are about to send is worth knowing either way, and it makes
            // the trade-off a bigger size buys immediately obvious.
            var quality = $"{spec.Height}p{spec.Fps:0}{(useAv1 ? " · AV1" : string.Empty)}";
            ShareResultSizeText.Text = $"{actualMb:0.#} MB · {quality}";
            if (ReferenceEquals(_shareCts, cts)) _shareCts = null;
            _sharePath = tempPath;
            ShareShowInFolderButton.Content = "Show in folder";
            ShareShowInFolderButton.IsEnabled = true;

            ShareThumbnail.Source = BuildPreviewImage();
            ShareThumbnail.IsVisible = ShareThumbnail.Source is not null;
            SizeSharePreviewBox();
            ShareDurationText.Text = FormatShareDuration(exportDuration);
            ShareDurationBadge.IsVisible = true;
        }
        catch (Exception error)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Error("Share: encode failed", error);
            ShareProgressPanel.IsVisible = false;
            if (ReferenceEquals(_shareCts, cts)) _shareCts = null;
            ShareShowInFolderButton.Content = "Show in folder";
            ShareShowInFolderButton.IsEnabled = false;
            ShareStatusText.Text = "Encode failed.";
        }
    }

    // The thumbnail is the whole source frame (MediaProbeService writes it
    // "scale=960:-2"), so a crop the user picked in the editor is not in it -
    // and the file being dragged out IS cropped. Cut the same window out of the
    // bitmap rather than showing a frame that is not what gets sent.
    private Avalonia.Media.IImage? BuildPreviewImage()
    {
        var source = _viewModel.SelectedThumbnail;
        if (source is null) return null;
        if (_viewModel.ActiveCropRect is not { } crop) return source;

        var sourceWidth = _viewModel.SelectedSourceWidth;
        var sourceHeight = _viewModel.SelectedSourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return source;

        var size = source.PixelSize;
        var scaleX = (double)size.Width / sourceWidth;
        var scaleY = (double)size.Height / sourceHeight;
        var x = Math.Clamp((int)Math.Round(crop.X * scaleX), 0, Math.Max(0, size.Width - 1));
        var y = Math.Clamp((int)Math.Round(crop.Y * scaleY), 0, Math.Max(0, size.Height - 1));
        var width = Math.Clamp((int)Math.Round(crop.Width * scaleX), 1, size.Width - x);
        var height = Math.Clamp((int)Math.Round(crop.Height * scaleY), 1, size.Height - y);
        return new CroppedBitmap(source, new PixelRect(x, y, width, height));
    }

    // Fits the preview's own aspect inside the fixed 424x238 slot (dialog is 480
    // wide with 28px margins either side). Vertical crops get narrower, never
    // taller, so picking an aspect never resizes the dialog under the cursor.
    private void SizeSharePreviewBox(bool useSourceDimensions = false)
    {
        const double MaximumWidth = 424;
        const double MaximumHeight = 238;

        var crop = useSourceDimensions ? null : _viewModel.ActiveCropRect;
        var width = crop?.Width ?? _viewModel.SelectedSourceWidth;
        var height = crop?.Height ?? _viewModel.SelectedSourceHeight;
        var aspect = width > 0 && height > 0 ? (double)width / height : 16.0 / 9.0;

        var boxHeight = MaximumHeight;
        var boxWidth = boxHeight * aspect;
        if (boxWidth > MaximumWidth)
        {
            boxWidth = MaximumWidth;
            boxHeight = boxWidth / aspect;
        }

        SharePreviewBox.Width = boxWidth;
        SharePreviewBox.Height = boxHeight;

        // The progress panel sits inside the box, so a 9:16 or 4:5 box has to
        // pull the bar in with it rather than let it run over the outline.
        var barWidth = Math.Min(160, Math.Max(0, boxWidth - 24));
        ShareProgressBar.Width = barWidth;
        ShareProgressPercentText.MaxWidth = Math.Max(0, boxWidth - 24);
        ShareProgressEtaText.MaxWidth = Math.Max(0, boxWidth - 24);
    }

    private static string FormatShareDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private void ShowInFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_shareCts is { IsCancellationRequested: false })
        {
            _shareCts.Cancel();
            _shareCts = null;
            if (_shareTempPath is { } tempPath) _ = DeleteWithRetryAsync(tempPath);
            _shareTempPath = null;
            _sharePath = null;
            ShareProgressPanel.IsVisible = false;
            ShareShowInFolderButton.Content = "Show in folder";
            ShareShowInFolderButton.IsEnabled = false;
            ShareStatusText.Text = "Encoding cancelled.";
            return;
        }

        if (_sharePath is { } path) ExplorerService.Open(path, selectFile: true);
    }

    private void Thumbnail_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPressPoint = e.GetPosition(ShareThumbnail);
        _dragPressEvent = e;
    }

    private async void Thumbnail_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPressPoint is not { } start || _dragPressEvent is not { } dragStart || e.GetCurrentPoint(ShareThumbnail).Properties.IsLeftButtonPressed != true) return;
        if (_sharePath is not { } sharePath) return;
        var current = e.GetPosition(ShareThumbnail);
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4) return;
        _dragPressPoint = null;
        _dragPressEvent = null;

        StartDragCursorWatch();
        try
        {
            // Preferred: the shell's own data object for the file, which
            // carries the translucent thumbnail that follows the cursor the
            // way dragging out of Explorer does. It blocks for the whole
            // gesture, same as Avalonia's version, so the bracketing below
            // still lines up.
            if (ShellFileDrag.TryDragFile(sharePath)) return;

            var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(sharePath)));
            if (file is null)
            {
                AppLog.Error($"Share: drag-out source disappeared: {sharePath}");
                return;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(dragStart, data, DragDropEffects.Copy);
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
            CloseDragOverlay();
        }
    }

    private void Thumbnail_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragPressPoint = null;
        _dragPressEvent = null;
    }

    // DoDragDrop blocks this method for the whole gesture, and during a drag
    // the app gets no pointer events at all (the OS owns the pointer), so
    // "is the cursor still over ClypDat" can only be answered by polling the
    // cursor against the window rect.
    private DispatcherTimer? _dragCursorWatch;
    private bool _dragLeftApp;

    private void StartDragCursorWatch()
    {
        _dragLeftApp = false;
        CloseDragOverlay();
        _dragOverlay = new ShareDragOverlayWindow(_coveredWindow ?? this);
        _dragOverlay.Show(this);
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

    private void CloseDragOverlay()
    {
        try { _dragOverlay?.Close(); } catch { /* already closing */ }
        _dragOverlay = null;
    }

    private void DragCursorWatch_OnTick(object? sender, EventArgs e)
    {
        if (_dragLeftApp) return;
        var handle = (_coveredWindow ?? this).TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(handle, out var rect)) return;
        if (cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom) return;

        // One-way: the overlay's whole job is getting ClypDat out of the way
        // while the user aims at another window, and it has done that as soon
        // as the cursor is off the app. Coming back over ClypDat mid-drag
        // (crossing it on the way to something else, or dropping the clip
        // back here) must not slam the panel up again, so the watch latches
        // off and stays off for the rest of this gesture.
        _dragLeftApp = true;
        CloseDragOverlay();
        StopDragCursorWatch();
    }
}
