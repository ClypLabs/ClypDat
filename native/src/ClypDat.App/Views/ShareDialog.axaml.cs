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
        DataContext = viewModel;
        PositionOverOwner(owner);
        Closed += (_, _) => CleanUp();
        Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(this, ShareScrim.Background, b => ShareScrim.Background = b);

        // Deliberately does NOT start encoding on open. Encoding is expensive
        // GPU work, and this app is usually running with the replay buffer
        // live while a game is in the foreground - kicking off a maximum-
        // effort NVENC job just because a dialog appeared spikes the GPU for
        // something the user has not asked for yet, and competes with the
        // capture encoder that is protecting their gameplay.
        Opened += (_, _) => SweepStaleShareTempFiles();
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

            if (result.ExitCode != 0)
            {
                _ = DeleteWithRetryAsync(tempPath);
                _shareTempPath = null;
                ShareProgressPanel.IsVisible = false;
                ShareStatusText.Text = string.IsNullOrWhiteSpace(result.Error) ? "Encode failed." : result.Error;
                return;
            }

            var spec = _viewModel.ComputeShareEncodeSpec(exportDuration.TotalSeconds, _viewModel.SelectedSourceWidth, _viewModel.SelectedSourceHeight, _viewModel.SelectedSourceFps, targetBytes, useAv1);
            var actualMb = actualBytes / 1_000_000.0;

            ShareProgressPanel.IsVisible = false;
            ShareStatusText.Text = "Drag this clip into any chat to upload it";
            // Resolution/fps is always shown, not just when downscaled - what
            // you are about to send is worth knowing either way, and it makes
            // the trade-off a bigger size buys immediately obvious.
            var quality = $"{spec.Height}p{spec.Fps:0}{(useAv1 ? " · AV1" : string.Empty)}";
            ShareResultSizeText.Text = $"{actualMb:0.#} MB · {quality}";
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

        StartDragCursorWatch();
        try
        {
            // Preferred: the shell's own data object for the file, which
            // carries the translucent thumbnail that follows the cursor the
            // way dragging out of Explorer does. It blocks for the whole
            // gesture, same as Avalonia's version, so the bracketing below
            // still lines up.
            if (ShellFileDrag.TryDragFile(tempPath)) return;

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
