using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using Eve.Capture.Abstractions;
using Eve.App.Services;
using Eve.App.ViewModels;
using LibVLCSharp.Shared;

namespace Eve.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _gameDetectionTimer;
    private readonly ForegroundGameDetector _gameDetector = new();
    private Cs2GsiListener? _cs2GsiListener;
    private PlaybackSession? _playback;
    private CancellationTokenSource? _playbackStartCts;
    private CancellationTokenSource? _editorSeekCts;
    private TimelineDragMode _timelineDragMode = TimelineDragMode.None;
    private bool _endedAtTrimBoundary;
    private bool _timelineWasPlayingBeforeDrag;
    private readonly Stopwatch _playheadClock = new();
    private TimeSpan _playheadBaseTime = TimeSpan.Zero;
    // Live-previews the actual video frame while dragging the playhead instead
    // of only updating the marker and seeking once on release - throttled since
    // PointerMoved can fire far faster than a LibVLC seek+settle round-trip can
    // keep up with; ApplyTimelineSeekAsync/PlaybackSession.SeekAsync already
    // cancel/supersede a still-in-flight seek when a newer one arrives, so a
    // throttle here just caps how often that cancel-and-restart happens rather
    // than needing any new synchronization of its own.
    private readonly Stopwatch _timelineScrubThrottle = new();
    // Lowered from 120ms alongside PlaybackSession's preview-mode seek wait
    // (see SeekAndWaitAsync) - the confirmation wait used to be the actual
    // bottleneck (up to 900ms, serialized behind _seekLock), so this throttle
    // never got a chance to matter. Now that a preview seek gets out of the
    // way quickly, this can drop closer to its intended job of pacing scrub
    // updates rather than pacing around a slow seek round-trip.
    private static readonly TimeSpan TimelineScrubMinInterval = TimeSpan.FromMilliseconds(60);
    private IReplayBuffer? _replayBuffer;
    private ReplayBackendOption _activeReplayBackend = ReplayBackendOption.Auto;
    private GlobalHotkeyService? _globalHotkey;
    private readonly HashSet<string> _capturedHotkeyKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _replayTransitioning;
    private bool _replayArmed;
    private readonly SemaphoreSlim _clipSaveLock = new(1, 1);
    private bool _updateDialogOpen;
    // Closing the window (the X button) hides to the tray instead of quitting,
    // so the replay buffer/Full Session keeps recording - matches the tray
    // icon's own "Open"/"Quit" menu, which otherwise had no way to actually be
    // reached since the X button always fully exited first. Only the tray's
    // own Quit item sets this true before closing for real.
    public bool AllowRealClose { get; set; }
    private List<(double StartSeconds, double EndSeconds)> _pausedRanges = new();
    private Window? _recordingPausedOverlay;
    public MainWindow()
    {
        InitializeComponent();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playbackTimer.Tick += (_, _) => SyncPlaybackPosition();
        _gameDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameDetectionTimer.Tick += (_, _) => UpdateDetectedGame();
        Opened += (_, _) =>
        {
            ViewModel?.UpdateCardLayout(Bounds.Width);
            InitializeReplayServices();
            UpdateDetectedGame();
            _gameDetectionTimer.Start();
            _ = EnsureLibraryFolderAsync();
            _ = RunStartupDialogsAsync();
            if (ViewModel is not null)
            {
                _gameDetector.ApplyCustomGameNames(ViewModel.Settings.GameCaptureOverrides);
                ViewModel.GameCatalogChanged += (_, _) => _gameDetector.ApplyCustomGameNames(ViewModel.Settings.GameCaptureOverrides);
                ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.Cs2AutoClipEnabled)) UpdateCs2AutoClipState();
                };
                UpdateCs2AutoClipState();
            }
        };
        // Tunnel, not bubble - a focused Button (Export, a transport button,
        // anything clicked most recently) otherwise intercepts Space itself
        // before this handler ever sees it (Button's own gesture recognizer
        // treats focused+Space as "activate me"), so Space would trigger
        // whatever was last clicked instead of always meaning play/pause.
        // Tunnel fires on the way down to the focused element, winning the race.
        // Avalonia's Button activates Space on KeyUp specifically, so KeyUp
        // needs the same Tunnel treatment as KeyDown - a plain (bubble) KeyUp
        // handler runs AFTER the focused Button's own KeyUp already fired
        // Click, too late to swallow it.
        AddHandler(KeyDownEvent, MainWindow_OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, MainWindow_OnKeyUp, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            SaveWindowBounds();
            ViewModel?.SaveSettings();
            if (!AllowRealClose)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
            }
        };
        Closed += (_, _) =>
        {
            _globalHotkey?.Dispose();
            _cs2GsiListener?.Dispose();
            _gameDetectionTimer.Stop();
            if (_replayBuffer is not null) _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
            _replayBuffer?.Dispose();
            _playback?.Dispose();
            _recordingPausedOverlay?.Close();
            ViewModel?.Dispose();
        };
        AddHandler(PointerPressedEvent, VolumeSlider_OnPointerPressedAny, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, VolumeSlider_OnPointerReleasedAny, RoutingStrategies.Tunnel, true);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private bool _gameDetectionInFlight;

    private async void UpdateDetectedGame()
    {
        if (ViewModel is null || _gameDetectionInFlight) return;
        _gameDetectionInFlight = true;
        GameDetection detection;
        try
        {
            detection = await Task.Run(() => _gameDetector.Detect());
        }
        finally
        {
            _gameDetectionInFlight = false;
        }

        if (ViewModel is null) return;
        ViewModel.ActiveGameDetection = detection;
        ViewModel.ActiveGame = detection.DisplayName;

        if (_replayArmed && detection.IsDetected && _replayBuffer is { IsRecording: false } && !_replayTransitioning)
        {
            _ = StartReplayBufferAsync(showErrors: false);
        }
        else if (_replayArmed && _replayBuffer is { IsRecording: true } && !detection.IsDetected && !_replayTransitioning)
        {
            _ = StopReplayBufferAsync();
        }

        UpdateCapturePauseState(detection);
    }

    private void UpdateCapturePauseState(GameDetection detection)
    {
        if (_replayBuffer is not { IsRecording: true }) return;
        var shouldPause = string.Equals(detection.ExeName, "cs2.exe", StringComparison.OrdinalIgnoreCase) && detection.IsDetected && !detection.IsForeground;
        _replayBuffer.SetCapturePaused(shouldPause);
    }

    private void InitializeReplayServices()
    {
        if (ViewModel is null || _replayBuffer is not null) return;

        _replayBuffer = ReplayBufferFactory.Create(ViewModel.CreateReplayConfig);
        _replayBuffer.RecordingStopped += ReplayBuffer_OnRecordingStopped;
        _activeReplayBackend = ReplayBufferFactory.ResolveEffectiveBackend(ViewModel.CreateReplayConfig());
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.SetHotkey(ViewModel.Settings.SaveReplayHotkey);
        _globalHotkey.Pressed += (_, _) => Dispatcher.UIThread.Post(() => _ = SaveReplayClipAsync(), DispatcherPriority.Send);
        try
        {
            _globalHotkey.Start();
        }
        catch
        {
            // Global hotkey failure should not block editor startup.
        }

        _replayArmed = true;
        ViewModel.RecorderStatus = "Replay Armed";
        UpdateDetectedGame();
    }

    private void RestartAppButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Restart failed", error);
        }
        finally
        {
            Close();
            Environment.Exit(0);
        }
    }

    private void EnsureReplayBufferMatchesGame()
    {
        if (ViewModel is null || _replayBuffer is null || _replayBuffer.IsRecording) return;
        var config = ViewModel.CreateReplayConfig();
        var desired = ReplayBufferFactory.ResolveEffectiveBackend(config);
        if (desired == _activeReplayBackend) return;

        AppLog.Info($"Replay backend switching: {_activeReplayBackend} -> {desired} for game={config.GameExecutableName}.");
        _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
        _replayBuffer.Dispose();
        _replayBuffer = ReplayBufferFactory.Create(ViewModel.CreateReplayConfig);
        _replayBuffer.RecordingStopped += ReplayBuffer_OnRecordingStopped;
        _activeReplayBackend = desired;
    }

    private async void ResetLibraryFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var path = DefaultLibraryFolder();
        Directory.CreateDirectory(path);
        LibraryLayout.EnsureRoots(path);
        await ViewModel.LoadLibraryFolderAsync(path);
    }

    private void ClearGameFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearGameFilters();
    }

    private async void FolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select library folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is { Length: > 0 } path)
        {
            await ViewModel!.LoadLibraryFolderAsync(path);
        }
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshLibraryAsync();
        }
    }

    private void Window_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ViewModel?.UpdateCardLayout(e.NewSize.Width);
        UpdateTimelineChrome();
    }

    private async void ReplayButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        // Stopping a Full Session recording can take real time (ffmpeg muxing
        // the whole session's audio) - without this guard, a click that
        // landed while a previous stop was still finalizing would see
        // IsRecording already false (it flips early, before the slow
        // finalization work) and start a SECOND recording session on top of
        // the first one's still-running finalization, which is exactly what
        // "won't turn off, repeated clicks freeze it" was.
        if (_replayTransitioning) return;
        InitializeReplayServices();
        if (_replayBuffer is null) return;

        if (_replayArmed || _replayBuffer.IsRecording)
        {
            _replayArmed = false;
            await StopReplayBufferAsync();
        }
        else
        {
            _replayArmed = true;
            ViewModel.RecorderStatus = "Replay Armed";
            if (ViewModel.ActiveGameDetection.IsDetected)
            {
                await StartReplayBufferAsync(showErrors: true);
            }
        }
    }

    private async Task StopReplayBufferAsync()
    {
        if (ViewModel is null || _replayBuffer is null || _replayTransitioning) return;
        _replayTransitioning = true;
        // Full Session recording's finalization (ffmpeg muxing the whole
        // session's audio against the video, "-c:v copy" but still real time
        // for a long session) runs inside _replayBuffer.StopAsync() below -
        // shown so a long stop reads as "still working" instead of looking
        // stuck and inviting more clicks.
        var wasFullSessionRecording = ViewModel.Settings.FullSessionRecordingEnabled;
        if (wasFullSessionRecording) ViewModel.RecorderStatus = "Saving Session...";
        try
        {
            if (_replayBuffer.IsRecording) await _replayBuffer.StopAsync();
            ViewModel.IsReplayRecording = false;
            ViewModel.RecorderStatus = _replayArmed ? "Replay Armed" : "Replay Off";
        }
        finally
        {
            _replayTransitioning = false;
        }
    }

    private async void RestartReplayBufferButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsReplayRecording) return;
        await StopReplayBufferAsync();
        await StartReplayBufferAsync(showErrors: true);
    }

    private async void ClipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveReplayClipAsync();
    }

    private async Task StartReplayBufferAsync(bool showErrors)
    {
        if (ViewModel is null) return;
        InitializeReplayServices();
        if (_replayBuffer is null) return;
        if (_replayTransitioning) return;

        try
        {
            _replayTransitioning = true;
            if (!ViewModel.ActiveGameDetection.IsDetected)
            {
                ViewModel.RecorderStatus = _replayArmed ? "Replay Armed" : "Replay Off";
                return;
            }
            EnsureReplayBufferMatchesGame();
            if (_replayBuffer is null) return;
            await EnsureLibraryFolderAsync();
            ApplyPrimaryCaptureBounds();
            await Task.Run(() => _replayBuffer.StartAsync());
            AppLog.Info("Replay started.");
            ViewModel.IsReplayRecording = _replayBuffer.IsRecording;
        }
        catch (Exception error)
        {
            AppLog.Error("Replay start failed", error);
            ViewModel.IsReplayRecording = false;
            if (showErrors)
            {
                await ShowMessageAsync("Replay unavailable", error.Message);
            }
        }
        finally
        {
            _replayTransitioning = false;
        }
    }

    private async Task SaveReplayClipAsync(string? autoClipLabel = null)
    {
        var isAutoClip = autoClipLabel is not null;
        // A replay save (segment hydrate/mux) can take 20-30+ seconds. Manual clip
        // presses reject outright while one's already running (spam-clicking
        // shouldn't queue a pile of saves), but auto-clip triggers queue instead -
        // a 3K's save still being in flight when the round's 4K/Ace happen a few
        // seconds later used to just silently drop those, because this used to be
        // "reject if busy" for everyone. Each distinct kill-streak milestone
        // should still get its own clip even if it has to wait its turn.
        if (isAutoClip)
        {
            await _clipSaveLock.WaitAsync();
        }
        else if (!await _clipSaveLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (ViewModel is null) return;
            InitializeReplayServices();
            if (_replayBuffer is null || !_replayBuffer.IsRecording)
            {
                // A background auto-clip trigger firing before the buffer is actually
                // recording (e.g. CS2 launched but EVE hasn't caught up yet) isn't
                // worth interrupting the user over - just drop it.
                if (isAutoClip) return;
                if (ViewModel.IsReplayRecording) ViewModel.IsReplayRecording = false;
                await ShowMessageAsync("Clip failed", _replayArmed ? "Replay is armed, but no game is being captured yet." : "Replay buffer is not running.");
                return;
            }

            var outputFolder = ViewModel.Settings.LibraryFolder;
            var folderReady = !string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder);

            try
            {
                if (!folderReady)
                {
                    await EnsureLibraryFolderAsync();
                    outputFolder = ViewModel.Settings.LibraryFolder;
                }
                LibraryLayout.EnsureRoots(outputFolder);
                outputFolder = LibraryLayout.ClipsRoot(outputFolder);

                AppLog.Info(isAutoClip ? $"Auto-clip triggered: {autoClipLabel}." : "Replay clip save requested.");

                // Windows Capture segments need a few seconds to concat/mux before
                // the clip lands in the library, so give instant feedback on the
                // hotkey press instead of waiting for that to finish.
                var notifiedEarly = _replayBuffer is WindowsReplayBuffer;
                if (notifiedEarly) ShowClipSavedNotification();

                var outputPath = await Task.Run(() => _replayBuffer.SaveReplayAsync(outputFolder, titleOverride: autoClipLabel));
                AppLog.Info($"Replay clip saved: {outputPath}");
                // "3K - Mirage" -> event type "3K", map dropped - the game name
                // (not the map) is what belongs next to it as the game label.
                var autoClipEventType = autoClipLabel?.Split(" - ", 2)[0];
                ClipInfoSidecar.Save(ViewModel.Settings.LibraryFolder, outputPath, new ClipInfo(
                    ViewModel.ActiveGameDetection.DisplayName,
                    autoClipEventType,
                    autoClipLabel ?? ViewModel.ActiveGameDetection.DisplayName,
                    File.GetCreationTimeUtc(outputPath)));
                await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
                if (!notifiedEarly) ShowClipSavedNotification();
            }
            catch (Exception error)
            {
                AppLog.Error("Replay clip save failed", error);
                if (!isAutoClip) await ShowMessageAsync("Clip failed", error.Message);
            }
        }
        finally
        {
            _clipSaveLock.Release();
        }
    }

    private void ShowClipSavedNotification()
    {
        if (ViewModel is null) return;
        if (ViewModel.Settings.EnableClipOverlaySound)
        {
            try
            {
                ClipNotificationSound.Play(ViewModel.Settings.ClipOverlayVolume);
            }
            catch (Exception error)
            {
                AppLog.Error("Clip notification sound failed", error);
            }
        }

        if (ViewModel.Settings.EnableClipOverlay)
        {
            try
            {
                ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition);
            }
            catch (Exception error)
            {
                AppLog.Error("Clip notification overlay failed", error);
            }
        }
    }

    private void ShowClipSavedOverlay(string position)
    {
        var badge = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#DD141D24"),
            BorderBrush = Avalonia.Media.Brush.Parse("#2C3B48"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Clip saved",
                        Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredWidth = badge.DesiredSize.Width;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;
        var marginDevicePixels = (int)Math.Round(24 * scaling);
        var widthDevicePixels = (int)Math.Round(desiredWidth * scaling);
        var x = string.Equals(position, "Top Left", StringComparison.OrdinalIgnoreCase)
            ? area.X + marginDevicePixels
            : area.X + area.Width - widthDevicePixels - marginDevicePixels;

        var overlay = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(x, area.Y + marginDevicePixels),
            Content = badge
        };

        overlay.Show();

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            overlay.Close();
        };
        closeTimer.Start();
    }

    private void ReplayBuffer_OnRecordingStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not null)
            {
                ViewModel.IsReplayRecording = false;
                ViewModel.RecorderStatus = _replayArmed ? "Replay Armed" : "Replay Off";
            }
        });
    }

    private async Task EnsureLibraryFolderAsync()
    {
        if (ViewModel is null) return;
        if (!string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder) && Directory.Exists(ViewModel.Settings.LibraryFolder)) return;

        var path = DefaultLibraryFolder();
        Directory.CreateDirectory(path);
        LibraryLayout.EnsureRoots(path);
        await ViewModel.LoadLibraryFolderAsync(path);
    }

    // First run: EVE gets a Videos\EVE folder with the standard Clips/VODs
    // layout, so recording never blocks behind a folder picker.
    private static string DefaultLibraryFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "EVE");

    private void TimelineSurface_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateTimelineChrome();
    }

    private async void ClipCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is CheckBox) return;
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        e.Handled = true;
        await OpenClipCardAsync(clip);
    }

    private async Task OpenClipCardAsync(ClipCardViewModel clip)
    {
        if (ViewModel is null) return;
        await ViewModel.OpenClipAsync(clip);
        QueueEditorPlayback();
    }

    private async void ClipContextExport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;
        await OpenClipCardAsync(clip);
        await ExportCurrentClipAsync();
    }

    private async void ClipContextOpen_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip }) return;
        await OpenClipCardAsync(clip);
    }

    private async void ClipContextRename_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        var newTitle = await PromptRenameAsync(clip.GameNameLabel);
        if (string.IsNullOrWhiteSpace(newTitle) || newTitle == clip.GameNameLabel) return;

        try
        {
            await ViewModel.RenameClipAsync(clip, newTitle);
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Rename failed", error.Message);
        }
    }

    private void ClipContextOpenLocation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip }) return;
        OpenInExplorer(clip.Path, selectFile: true);
    }

    private async void ClipContextDelete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        var confirmed = await ConfirmDeleteAsync(clip.Name);
        if (!confirmed) return;

        try
        {
            await ViewModel.DeleteClipAsync(clip);
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Delete failed", error.Message);
        }
    }

    private void ClipCard_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ClipCardViewModel clip }) return;
        clip.IsHovered = true;
    }

    private void ClipCard_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ClipCardViewModel clip }) return;
        clip.IsHovered = false;
    }

    private void ClipCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ClipCardViewModel clip, IsChecked: var isChecked } && ViewModel is not null)
        {
            ViewModel.SetClipSelected(clip, isChecked == true);
        }
    }

    private void GroupCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ClipGroupViewModel group, IsChecked: var isChecked } && ViewModel is not null)
        {
            ViewModel.ToggleGroupSelection(group, isChecked == true);
        }
    }

    private async void DeleteSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasSelection) return;
        var confirmed = await ConfirmDeleteAsync(ViewModel.SelectionSummary);
        if (!confirmed) return;

        try
        {
            await ViewModel.DeleteSelectedAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Delete failed", error.Message);
        }
    }

    private async void RenameAllClipsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanRenameAllClips) return;
        var dialog = CreateDialog("Rename all clips?", "This renames every video in the current library to the selected filename scheme. Existing files are never overwritten.", true);
        if (!await dialog.ShowDialog<bool>(this)) return;
        await ViewModel.RenameAllClipsAsync();
    }

    private WindowState _preFullscreenWindowState = WindowState.Normal;

    private void FullscreenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullscreenWindowState;
        }
        else
        {
            _preFullscreenWindowState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void CloseEditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SaveSelectedClipEditState();
        StopEditorPlayback(stopPlaybackAsync: true);
        ViewModel?.CloseEditor();
    }

    // The EVE logo button is a universal "go back to Library" from anywhere
    // else in the app (editor or Settings). Opening Settings has its own
    // dedicated button now (bottom-left of the Library).
    private void LibraryHomeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        if (ViewModel.IsEditorVisible)
        {
            CloseEditorButton_OnClick(sender, e);
            return;
        }

        if (ViewModel.IsSettingsVisible)
        {
            ViewModel.CloseSettings();
        }
    }

    private async void OpenSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.OpenSettings();
        await ViewModel.RefreshOpenProcessesAsync();
    }


    private void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseSettings();
    }

    private void SettingsNavButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } && ViewModel is not null)
        {
            ViewModel.SelectSettingsSection(section);
        }
    }

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AppLog.OpenFolder();
    }

    private void ToggleCs2CardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.Cs2CardExpanded = !ViewModel.Cs2CardExpanded;
    }

    private void ToggleCs2AllKillsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.Cs2AllKillsExpanded = !ViewModel.Cs2AllKillsExpanded;
    }

    private async void ScanMedalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.ScanForMedalClipsAsync();
    }

    private async void ImportMedalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await EnsureLibraryFolderAsync();
        await ViewModel.ImportSelectedMedalClipsAsync();
    }

    private async void BrowseCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select game executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable") { Patterns = new[] { "*.exe" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is not { Length: > 0 } path) return;

        ViewModel.NewCustomGameExecutable = Path.GetFileName(path);
        ViewModel.NewCustomGameDisplayName = Path.GetFileNameWithoutExtension(path);
        ViewModel.AddCustomGame();
    }

    private void AddGameFromProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddGameFromProcess();
    }

    private void RemoveCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameBackendRowViewModel row } && ViewModel is not null)
        {
            ViewModel.RemoveCustomGame(row);
        }
    }

    private void UpdateCs2AutoClipState()
    {
        if (ViewModel is null) return;

        if (!ViewModel.Settings.Cs2AutoClip.Enabled)
        {
            if (_cs2GsiListener is not null)
            {
                _cs2GsiListener.AutoClipTriggered -= Cs2GsiListener_OnAutoClipTriggered;
                _cs2GsiListener.Stop();
            }

            ViewModel.Cs2GsiStatusText = string.Empty;
            return;
        }

        _cs2GsiListener ??= new Cs2GsiListener(() => ViewModel.Settings.Cs2AutoClip);
        if (_cs2GsiListener.IsListening) return;

        var port = ViewModel.Settings.Cs2AutoClip.GsiPort;
        if (!_cs2GsiListener.Start(port))
        {
            ViewModel.Cs2GsiStatusText = $"Auto-clip listener couldn't start on port {port} - it may already be in use. Check the log.";
            return;
        }

        _cs2GsiListener.AutoClipTriggered += Cs2GsiListener_OnAutoClipTriggered;
        Cs2GsiDeployer.TryDeploy(port, out var statusMessage);
        ViewModel.Cs2GsiStatusText = statusMessage;
    }

    private void Cs2GsiListener_OnAutoClipTriggered(object? sender, string label)
    {
        Dispatcher.UIThread.Post(() => _ = SaveReplayClipAsync(label));
    }

    private async void CheckUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _updateDialogOpen) return;

        AppUpdateInfo? update;
        try
        {
            update = await AppUpdateService.CheckAsync();
        }
        catch (Exception error)
        {
            AppLog.Error("Update check failed", error);
            await ShowMessageAsync("Update check failed", error.Message);
            return;
        }

        if (update is null)
        {
            var current = AppUpdateService.CurrentVersion;
            await ShowMessageAsync("You're up to date", $"EVE {current.Major}.{current.Minor}.{current.Build} is the latest version.");
            return;
        }

        _updateDialogOpen = true;
        try
        {
            var dialog = CreateUpdateDialog(update);
            await dialog.ShowDialog(this);
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private void OpenGitHubButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Stormanzanii/EVE") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error("Open GitHub failed", error);
        }
    }

    private void OpenLicensesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-LICENSES.md");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error("Open licenses failed", error);
        }
    }

    private void LicenseLinkText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error($"Open license link failed: {url}", error);
        }
    }

    private void LibraryPathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder)) return;
        OpenInExplorer(ViewModel.Settings.LibraryFolder, selectFile: false);
    }

    private void EditorPathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        OpenInExplorer(ViewModel.SelectedVideoPath, selectFile: true);
    }

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _capturedHotkeyKeys.Clear();
        if (ViewModel is not null) ViewModel.IsCapturingHotkey = true;
        Focus();
    }

    private void AddExcludedProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (this.FindControl<TextBox>("ExcludedProcessTextBox") is not { } textBox) return;
        ViewModel.AddExcludedProcess(textBox.Text ?? string.Empty);
        textBox.Text = string.Empty;
    }

    private void AddSelectedProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddSelectedProcessExclusion();
    }

    private async void RefreshProcessesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshOpenProcessesAsync();
        }
    }

    private void RemoveExcludedProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string processName })
        {
            ViewModel?.RemoveExcludedProcess(processName);
        }
    }

    private void AddSelectedChatProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddSelectedChatProcess();
    }

    private void RemoveChatAudioAppButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string appName })
        {
            ViewModel?.RemoveChatAudioApp(appName);
        }
    }

    private void AddSelectedMicrophoneButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddSelectedMicrophone();
    }

    private void RemoveMicrophoneButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioDeviceOption device })
        {
            ViewModel?.RemoveMicrophone(device.Id);
        }
    }


    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.IsCapturingHotkey == true)
        {
            if (e.Key == Key.Escape)
            {
                _capturedHotkeyKeys.Clear();
                ViewModel.IsCapturingHotkey = false;
                e.Handled = true;
                return;
            }

            _capturedHotkeyKeys.Add(HotkeyCombo.NormalizeKey(e.Key.ToString()));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is not null && !IsTypingInTextInput(e.Source))
        {
            if (ViewModel.IsSettingsVisible)
            {
                ViewModel.CloseSettings();
                e.Handled = true;
                return;
            }

            if (ViewModel.IsEditorVisible)
            {
                ViewModel.SaveSelectedClipEditState();
                StopEditorPlayback(stopPlaybackAsync: true);
                ViewModel.CloseEditor();
                e.Handled = true;
                return;
            }
        }

        // Space is reserved app-wide for play/pause and must never activate
        // whatever control currently has keyboard focus instead (a Settings
        // toggle, "Refresh", whatever was last clicked) - swallow it here
        // unconditionally, before the Editor-only guard below decides whether
        // it actually does anything.
        if (e.Key == Key.Space && ViewModel is not null && !IsTypingInTextInput(e.Source))
        {
            e.Handled = true;
        }

        if (ViewModel is null ||
            !ViewModel.IsEditorVisible ||
            !ViewModel.Settings.EnableEditorKeyboardShortcuts ||
            IsTypingInTextInput(e.Source))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                _endedAtTrimBoundary = false;
                var leftWasPlaying = ViewModel.IsPlaying;
                ViewModel.SeekBySeconds(-1);
                _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, leftWasPlaying);
                e.Handled = true;
                break;
            case Key.Right:
                _endedAtTrimBoundary = false;
                var rightWasPlaying = ViewModel.IsPlaying;
                ViewModel.SeekBySeconds(1);
                _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, rightWasPlaying);
                e.Handled = true;
                break;
            case Key.Space:
                PlayPauseButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void MainWindow_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.IsCapturingHotkey == true)
        {
            if (_capturedHotkeyKeys.Count == 0) return;

            var hotkey = HotkeyCombo.Normalize(_capturedHotkeyKeys);
            if (!string.IsNullOrWhiteSpace(hotkey))
            {
                ViewModel.SetHotkey(hotkey);
                _globalHotkey?.SetHotkey(hotkey);
            }
            e.Handled = true;
            return;
        }

        // Avalonia's Button activates Space on KeyUp, not KeyDown - suppressing
        // Space only in MainWindow_OnKeyDown (which already does the actual
        // play/pause) wasn't enough, since the focused Button's own KeyUp
        // handling still ran afterward and fired its own Click too (whatever
        // was last clicked - Export, a transport button - "opened" again).
        // Swallowing Space here on Tunnel, same as KeyDown, closes that gap.
        // Unconditional (not gated to Editor) for the same reason as the
        // KeyDown swallow - Space must never activate whatever control has
        // focus anywhere in the app, not just in the Editor.
        if (e.Key == Key.Space && ViewModel is not null && !IsTypingInTextInput(e.Source))
        {
            e.Handled = true;
        }
    }

    private async void PlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (_playback is null)
        {
            await StartEditorPlaybackAsync(CancellationToken.None);
            return;
        }

        if (ViewModel.IsPlaying)
        {
            // ViewModel.CurrentTime while playing is SmoothPlaybackPosition() -
            // a software stopwatch projection kept smooth for the UI, not
            // reconciled against libvlc's actual decode position on every
            // tick. It can drift from the real position by more than
            // PlayFrom's 150ms needsSeek threshold over a longer playback
            // stretch, which was turning an ordinary pause-then-resume into
            // an unwanted real seek - the ~1s "snap" the video visibly does
            // before landing. Pausing at the actual position instead of the
            // smoothed estimate keeps resume within that threshold so it can
            // stay a plain unpause.
            var pauseTime = _playback.Position;
            _playback.Pause();
            ViewModel.CurrentTime = pauseTime;
            SetPlayheadBase(pauseTime);
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
            return;
        }

        var startTime = ViewModel.CurrentTime;
        if (_endedAtTrimBoundary ||
            (_playback.IsEnded && ViewModel.TrimEnd > TimeSpan.Zero && startTime >= ViewModel.TrimEnd - TimeSpan.FromMilliseconds(80)))
        {
            startTime = ViewModel.TrimStart;
            ViewModel.CurrentTime = startTime;
        }

        _endedAtTrimBoundary = false;
        _playback.PlayFrom(startTime);
        StartPlayheadClock(startTime);
        ViewModel.IsPlaying = true;
        _playbackTimer.Start();
    }

    private void RestartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        ViewModel.RestartPlayback();
        if (_playback is not null)
        {
            _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, ViewModel.IsPlaying);
        }
    }

    private void StepBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        var wasPlaying = ViewModel.IsPlaying;
        ViewModel.SeekBySeconds(-5);
        _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
    }

    private void StepForwardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        var wasPlaying = ViewModel.IsPlaying;
        ViewModel.SeekBySeconds(5);
        _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
    }

    private void EndButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var wasPlaying = ViewModel.IsPlaying;
        _endedAtTrimBoundary = true;
        ViewModel.CurrentTime = ViewModel.TrimEnd > TimeSpan.Zero ? ViewModel.TrimEnd : ViewModel.Duration;
        _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
    }

    private void TimelineSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.Playhead;
        _timelineWasPlayingBeforeDrag = ViewModel.IsPlaying;
        _endedAtTrimBoundary = false;
        if (_timelineWasPlayingBeforeDrag)
        {
            _playback?.Pause();
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
        }
        UpdateTimelineFromPointer(e, TimelineDragMode.Playhead);
        _timelineScrubThrottle.Restart();
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimStartHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimStart;
        _timelineWasPlayingBeforeDrag = ViewModel.IsPlaying;
        _endedAtTrimBoundary = false;
        if (_timelineWasPlayingBeforeDrag)
        {
            _playback?.Pause();
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
        }
        UpdateTimelineFromPointer(e, TimelineDragMode.TrimStart);
        _timelineScrubThrottle.Restart();
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimEndHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimEnd;
        _timelineWasPlayingBeforeDrag = ViewModel.IsPlaying;
        _endedAtTrimBoundary = false;
        if (_timelineWasPlayingBeforeDrag)
        {
            _playback?.Pause();
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
        }
        UpdateTimelineFromPointer(e, TimelineDragMode.TrimEnd);
        _timelineScrubThrottle.Restart();
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TimelineSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None || ViewModel is null) return;
        UpdateTimelineFromPointer(e, _timelineDragMode);

        // Live-preview the actual frame while dragging instead of leaving the
        // video dead until release - always paused/silent (resumePlayback:
        // false) during the drag itself, matching the pause-on-drag-start
        // behavior above; PointerReleased below issues the real, resume-aware
        // seek once the user lets go.
        if (_timelineScrubThrottle.Elapsed < TimelineScrubMinInterval) return;
        _timelineScrubThrottle.Restart();
        _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, resumePlayback: false, isPreview: true);
    }

    private async void TimelineSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None) return;
        var mode = _timelineDragMode;
        var wasPlaying = _timelineWasPlayingBeforeDrag;
        UpdateTimelineFromPointer(e, _timelineDragMode);

        // Drag mode/capture must clear BEFORE the seek await below, not after -
        // otherwise the gesture is still "active" for the whole async seek
        // (TimelineSurface_OnPointerMoved keeps acting on it, and the pointer is
        // still captured), so any mouse movement during that window keeps
        // dragging the seeker with no button held. Seeking right at/near a
        // clip's end is the slow case that made this window wide enough to hit.
        _timelineDragMode = TimelineDragMode.None;
        _timelineWasPlayingBeforeDrag = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (mode == TimelineDragMode.Playhead && ViewModel is not null)
        {
            await ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
        }
        else if (ViewModel is not null)
        {
            await ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
            ViewModel.SaveSelectedClipEditState();
        }
    }

    private void TrackVolume_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Slider { DataContext: TrackLaneViewModel track })
        {
            track.ShowVolumePercent = true;
            UpdateVolumeBadgePosition((Slider)sender, track);
            e.Handled = false;
        }
    }

    private void TrackVolume_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider { DataContext: TrackLaneViewModel track })
        {
            track.ShowVolumePercent = false;
            ViewModel?.SaveSelectedClipEditState();
            e.Pointer.Capture(null);
            e.Handled = false;
        }
    }

    private void VolumeSlider_OnPointerPressedAny(object? sender, PointerPressedEventArgs e)
    {
        var slider = (e.Source as Visual)?.FindAncestorOfType<Slider>();
        if (slider?.DataContext is not TrackLaneViewModel track || !track.IsAudio) return;
        track.ShowVolumePercent = true;
        UpdateVolumeBadgePosition(slider, track);
    }

    private void VolumeSlider_OnPointerReleasedAny(object? sender, PointerReleasedEventArgs e)
    {
        var slider = (e.Source as Visual)?.FindAncestorOfType<Slider>();
        if (slider?.DataContext is not TrackLaneViewModel track || !track.IsAudio) return;
        track.ShowVolumePercent = false;
        ViewModel?.SaveSelectedClipEditState();
    }

    private void TrackVolume_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Slider { DataContext: TrackLaneViewModel track } slider && track.ShowVolumePercent)
        {
            UpdateVolumeBadgePosition(slider, track);
        }
    }

    private void TrackVolume_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || sender is not Slider { DataContext: TrackLaneViewModel track } slider) return;
        track.VolumePercent = Math.Clamp(slider.Value, 0, 150);
        UpdateVolumeBadgePosition(slider, track);
        _playback?.SetTrackVolume(track.StreamIndex, track.EffectiveVolumePercent);
    }

    private void TrackMuteToggle_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackLaneViewModel track }) return;
        track.IsMuted = !track.IsMuted;
        _playback?.SetTrackVolume(track.StreamIndex, track.EffectiveVolumePercent);
        e.Handled = true;
    }

    private static void UpdateVolumeBadgePosition(Slider slider, TrackLaneViewModel track, double? pointerX = null)
    {
        var width = Math.Max(1, slider.Bounds.Width);
        var thumbX = pointerX ?? width * Math.Clamp(track.VolumePercent / 150d, 0, 1);
        var badgeX = thumbX > width - 48 ? thumbX - 48 : thumbX + 10;
        track.VolumeBadgeX = Math.Clamp(badgeX, 0, Math.Max(1, width - 38));
    }

    private async void ExportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ExportCurrentClipAsync();
    }

    private async Task ExportCurrentClipAsync()
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        var libraryRoot = string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder)
            ? DefaultLibraryFolder()
            : ViewModel.Settings.LibraryFolder;
        LibraryLayout.EnsureRoots(libraryRoot);

        var sourceInfo = ClipInfoSidecar.Load(libraryRoot, ViewModel.SelectedVideoPath);
        var game = ResolveExportGame(ViewModel.SelectedVideoPath, sourceInfo);
        var exportFolder = Path.Combine(LibraryLayout.ClipsRoot(libraryRoot), ClipFileNaming.BuildBaseName(game));
        Directory.CreateDirectory(exportFolder);
        var suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(exportFolder);
        var exportTimestamp = DateTime.Now;
        var suggestedFileName = ClipFileNaming.BuildFileName(
            ViewModel.EditorTitle,
            exportTimestamp,
            ".mp4",
            ViewModel.Settings.ClipFileNameScheme,
            ViewModel.Settings.CustomClipFileNameTemplate,
            game);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export clip",
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = suggestedStartLocation,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("MP4 video") { Patterns = new[] { "*.mp4" } }
            }
        });
        if (file?.Path.LocalPath is not { Length: > 0 } outputPath) return;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(outputPath)))
        {
            outputPath = Path.ChangeExtension(outputPath, ".mp4");
        }

        ViewModel.IsExporting = true;
        try
        {
            _playback?.Pause();
            ViewModel.IsPlaying = false;
            var args = ViewModel.BuildExportArguments(outputPath);
            var result = await RunProcessAsync("ffmpeg", args);
            if (result.ExitCode != 0)
            {
                await ShowMessageAsync("Export failed", string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed." : result.Error);
            }
            else
            {
                ClipInfoSidecar.Save(libraryRoot, outputPath, new ClipInfo(game, null, ViewModel.EditorTitle, exportTimestamp));
                if (IsPathWithinLibrary(outputPath, libraryRoot)) await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
                OpenInExplorer(outputPath, selectFile: true);
            }
        }
        finally
        {
            ViewModel.IsExporting = false;
        }
    }

    private static string ResolveExportGame(string sourcePath, ClipInfo? sourceInfo)
    {
        if (!string.IsNullOrWhiteSpace(sourceInfo?.GameDisplayName) && !MedalImportService.IsStructuralFolderName(sourceInfo.GameDisplayName))
        {
            return sourceInfo.GameDisplayName;
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(sourcePath));
        return string.IsNullOrWhiteSpace(parent) ||
               MedalImportService.IsStructuralFolderName(parent) ||
               string.Equals(parent, "Clips", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parent, "VODs", StringComparison.OrdinalIgnoreCase)
            ? "Unknown Game"
            : parent;
    }

    private static bool IsPathWithinLibrary(string path, string libraryRoot)
    {
        var relative = Path.GetRelativePath(libraryRoot, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private async Task<bool> ConfirmDeleteAsync(string summary)
    {
        var dialog = CreateDialog("Delete clips?", $"{summary}\n\nThis permanently deletes the file(s).", true);
        var result = await dialog.ShowDialog<bool>(this);
        return result;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message, false);
        await dialog.ShowDialog<bool>(this);
    }

    private async Task<string?> PromptRenameAsync(string currentTitle)
    {
        var window = new Window
        {
            Title = "Rename clip",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brush.Parse("#111920")
        };

        var textBox = new TextBox
        {
            Text = currentTitle,
            Watermark = "Clip title"
        };

        var rename = new Button
        {
            Content = "Rename",
            Width = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var cancel = new Button { Content = "Cancel", Width = 96 };

        rename.Click += (_, _) => window.Close(textBox.Text);
        cancel.Click += (_, _) => window.Close(null);
        textBox.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Enter) window.Close(textBox.Text);
            else if (keyArgs.Key == Key.Escape) window.Close(null);
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, rename }
        };

        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = "Rename clip",
                    Foreground = Avalonia.Media.Brush.Parse("#DDE8F5"),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 16
                },
                textBox,
                buttons
            }
        };

        window.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        return await window.ShowDialog<string?>(this);
    }

    private async Task RunStartupDialogsAsync()
    {
        if (ViewModel is not null && !ViewModel.Settings.HasSeenOnboarding)
        {
            ViewModel.StartOnboarding();
        }

        await CheckForUpdatesAsync();
    }

    private void ShowWalkthroughButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.StartOnboarding();
    }

    private void OnboardingBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OnboardingBack();
    }

    private void OnboardingNextButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OnboardingNext();
    }

    private void OnboardingSkipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.FinishOnboarding();
    }

    private void AddExcludedProcessOnboardingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (this.FindControl<TextBox>("OnboardingExcludedProcessTextBox") is not { } textBox) return;
        ViewModel.AddExcludedProcess(textBox.Text ?? string.Empty);
        textBox.Text = string.Empty;
    }

    private async Task CheckForUpdatesAsync()
    {
        if (ViewModel is null || _updateDialogOpen) return;
        AppUpdateInfo? update;
        try
        {
            update = await AppUpdateService.CheckAsync();
        }
        catch (Exception error)
        {
            AppLog.Error("Update check failed", error);
            return;
        }

        if (update is null) return;
        if (string.Equals(ViewModel.Settings.IgnoredUpdateVersion, update.TagName, StringComparison.OrdinalIgnoreCase)) return;

        _updateDialogOpen = true;
        try
        {
            var dialog = CreateUpdateDialog(update);
            await dialog.ShowDialog(this);
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private Window CreateUpdateDialog(AppUpdateInfo update)
    {
        var window = new Window
        {
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brush.Parse("#111920"),
            SystemDecorations = SystemDecorations.Full,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = -1,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
            TransparencyLevelHint = new[] { Avalonia.Controls.WindowTransparencyLevel.None }
        };

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Height = 40,
            Background = Avalonia.Media.Brush.Parse("#0C1319")
        };
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };
        var titleIcon = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://EVE/Assets/eve-icon-24.png"))), Width = 16, Height = 16, Margin = new Avalonia.Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock { Text = "Update available", Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleLeft = new StackPanel { Orientation = Orientation.Horizontal, Children = { titleIcon, titleText } };
        Grid.SetColumn(titleLeft, 0);
        var closeButton = new Button { Content = "✕", Width = 40, Height = 40, Padding = new Avalonia.Thickness(0), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(0), Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        titleBar.Children.Add(titleLeft);
        titleBar.Children.Add(closeButton);

        var statusText = new TextBlock
        {
            Text = string.Empty,
            Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
            FontSize = 12,
            IsVisible = false
        };
        var progressBar = new ProgressBar { IsVisible = false, Minimum = 0, Maximum = 100, CornerRadius = new Avalonia.CornerRadius(3), Height = 6 };

        var updateButton = new Button { Name = "UpdateNowButton", Content = "Update Now", Width = 120, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "primaryButton" } };
        var laterButton = new Button { Content = "Remind Me Later", Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ignoreButton = new Button { Content = "Skip This Version", HorizontalContentAlignment = HorizontalAlignment.Center };

        laterButton.Click += (_, _) => window.Close();
        ignoreButton.Click += (_, _) =>
        {
            if (ViewModel is not null)
            {
                ViewModel.Settings.IgnoredUpdateVersion = update.TagName;
                ViewModel.SaveSettings();
            }
            window.Close();
        };
        updateButton.Click += async (_, _) =>
        {
            updateButton.IsEnabled = false;
            laterButton.IsEnabled = false;
            ignoreButton.IsEnabled = false;
            statusText.IsVisible = true;
            progressBar.IsVisible = true;
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                statusText.Text = value.Status;
                progressBar.IsIndeterminate = value.Percentage is null;
                if (value.Percentage is not null) progressBar.Value = value.Percentage.Value * 100;
            });

            try
            {
                await AppUpdateService.DownloadAndRestartAsync(update, progress);
                window.Close();
            }
            catch (Exception error)
            {
                AppLog.Error("Update install failed", error);
                await ShowMessageAsync("Update failed", $"EVE could not install the update.\n\n{error.Message}");
                updateButton.IsEnabled = true;
                laterButton.IsEnabled = true;
                ignoreButton.IsEnabled = true;
                statusText.IsVisible = false;
                progressBar.IsVisible = false;
            }
        };

        var notesPanel = new StackPanel { Spacing = 6, IsVisible = update.ReleaseNotes.Count > 0 };
        foreach (var note in update.ReleaseNotes)
        {
            notesPanel.Children.Add(new TextBlock
            {
                Text = $"• {note}",
                Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"),
                FontSize = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
        }

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 20, 22, 22),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"EVE {FormatVersion(update.LatestVersion)} is available",
                    Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 18
                },
                new TextBlock
                {
                    Text = $"You're on {FormatVersion(update.CurrentVersion)}.",
                    Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
                    FontSize = 13
                },
                notesPanel,
                statusText,
                progressBar,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ignoreButton, laterButton, updateButton }
                }
            }
        };

        var notesScroll = new ScrollViewer { Content = body, MaxHeight = 520 };

        window.Content = new DockPanel
        {
            Children =
            {
                titleBar,
                notesScroll
            }
        };
        DockPanel.SetDock(titleBar, Dock.Top);

        return window;
    }

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private void QueueEditorPlayback()
    {
        _playbackStartCts?.Cancel();
        _playbackStartCts?.Dispose();
        var cts = new CancellationTokenSource();
        _playbackStartCts = cts;

        // Background priority deliberately deprioritized the actual video decode
        // start until after the editor panel had already finished rendering -
        // meant to keep the open transition feeling snappy, but it meant nothing
        // even started loading the clip until the (now-empty) editor was already
        // on screen. Default runs it as soon as pending input/layout work is
        // done instead of waiting for a full render pass, so decode starts
        // essentially in parallel with the panel appearing instead of after it.
        Dispatcher.UIThread.Post(
            async () =>
            {
                if (cts.IsCancellationRequested) return;
                await StartEditorPlaybackAsync(cts.Token);
            },
            DispatcherPriority.Default);
    }

    private async Task StartEditorPlaybackAsync(CancellationToken cancellationToken)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        if (cancellationToken.IsCancellationRequested) return;

        StopEditorPlayback(cancelQueuedStart: false);

        try
        {
            // Reused across editor opens instead of constructing a fresh
            // PlaybackSession every time - PlaybackSession's constructor spins up a
            // whole new LibVLC engine + MediaPlayer, which was the bulk of the
            // "video stays black for a moment" delay on every single clip open.
            // LoadVideo() already fully tears down and replaces the previous Media
            // internally, so the same instance is safe to reuse.
            var playback = _playback ?? new PlaybackSession();
            playback.LoadVideo(ViewModel.SelectedVideoPath);
            _playback = playback;
            _pausedRanges = LoadPausedRanges(ViewModel.SelectedVideoPath);
            ViewModel.IsRecordingPausedAtCurrentTime = false;
            // Redundant with StopEditorPlayback's own Hide() above, but closes
            // a real race: a timer tick already queued/dispatched right before
            // _playbackTimer.Stop() took effect can still fire once more using
            // the PREVIOUS clip's now-stale _pausedRanges, briefly reshowing
            // the overlay with wrong data - right as the editor's own layout
            // (EditorVideoView's bounds) may not have settled yet either,
            // which is exactly the "flickers over the library grid" symptom.
            _recordingPausedOverlay?.Hide();
            AppLog.Info($"Editor open: {ViewModel.SelectedVideoPath}");
            EditorVideoView.MediaPlayer = playback.VideoPlayer;
            var audioTracks = ViewModel.TimelineTracks
                .Where(track => track.IsAudio)
                .Select(track => new AudioPreviewTrack(track.StreamIndex, track.VolumePercent))
                .ToArray();
            if (cancellationToken.IsCancellationRequested) return;

            ViewModel.IsEditorVideoLoading = true;
            // Playing fires on the state transition alone, not on an actual
            // decoded frame reaching the screen - fine the first time (a fresh
            // PlaybackSession's own engine-startup latency happens to cover the
            // gap), but on every open after that the session/vout are already
            // warm, so Playing can fire before the NEW clip's first real frame
            // is ready and the placeholder drops early onto a black video view.
            // TimeChanged only fires once the position actually advances, which
            // requires real decode progress - same signal SeekAndWaitAsync uses
            // to confirm a seek has actually landed, not just been requested.
            // Scoped to this one load attempt (not a persistent subscription
            // on the reused PlaybackSession) so a superseded/cancelled open's
            // late-firing event can't wrongly clear a NEWER open's loading
            // flag - the cancellation check below guards that.
            void OnTimeChanged(object? _, MediaPlayerTimeChangedEventArgs __)
            {
                playback.VideoPlayer.TimeChanged -= OnTimeChanged;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (ViewModel is not null) ViewModel.IsEditorVideoLoading = false;
                });
            }
            playback.VideoPlayer.TimeChanged += OnTimeChanged;

            playback.PlayFrom(ViewModel.CurrentTime);
            StartPlayheadClock(ViewModel.CurrentTime);
            _endedAtTrimBoundary = false;
            ViewModel.IsPlaying = true;
            _playbackTimer.Start();
            _ = LoadEditorAudioAsync(playback, ViewModel.SelectedVideoPath, audioTracks, cancellationToken);
            await Task.Delay(200, cancellationToken);
            if (playback.Duration > TimeSpan.Zero && IsPlausibleDuration(playback.Duration, ViewModel.Duration))
            {
                ViewModel.SetDuration(playback.Duration);
            }
            UpdateTimelineChrome();
        }
        catch (Exception error)
        {
            AppLog.Error("Editor playback failed", error);
            StopEditorPlayback();
            await ShowMessageAsync("Playback unavailable", error.Message);
        }
    }

    private async Task LoadEditorAudioAsync(
        PlaybackSession playback,
        string videoPath,
        IReadOnlyList<AudioPreviewTrack> audioTracks,
        CancellationToken cancellationToken)
    {
        try
        {
            await playback.LoadAudioAsync(videoPath, audioTracks, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _playback != playback) return;
            playback.SyncAndPlayMixedAudio();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppLog.Error("Editor audio preview failed", error);
            await Dispatcher.UIThread.InvokeAsync(() => ShowMessageAsync("Audio preview unavailable", error.Message));
        }
    }

    private void StopEditorPlayback(bool cancelQueuedStart = true, bool stopPlaybackAsync = false)
    {
        if (cancelQueuedStart)
        {
            _playbackStartCts?.Cancel();
            _playbackStartCts?.Dispose();
            _playbackStartCts = null;
        }
        _editorSeekCts?.Cancel();
        _editorSeekCts?.Dispose();
        _editorSeekCts = null;
        _playbackTimer.Stop();
        _playheadClock.Stop();
        _endedAtTrimBoundary = false;
        // Stop and detach the view instead of disposing - the session (and its
        // underlying LibVLC engine) stays alive and gets reused on the next
        // editor open instead of being torn down and rebuilt from scratch.
        // VideoPlayer.Stop() is a genuinely blocking libvlc call (real time
        // spent tearing down decode/output threads once a clip's actually
        // been playing) - fine to eat synchronously when a LoadVideo() is
        // about to run right after on this same thread anyway (it stops
        // internally too either way), but doing it synchronously on editor
        // close just freezes the UI thread for however long libvlc takes to
        // unwind, well after the editor should already look closed.
        if (stopPlaybackAsync)
        {
            var playback = _playback;
            if (playback is not null) _ = Task.Run(() => playback.Stop());
        }
        else
        {
            _playback?.Stop();
        }
        EditorVideoView.MediaPlayer = null;
        _recordingPausedOverlay?.Hide();
        if (ViewModel is not null)
        {
            ViewModel.IsPlaying = false;
            ViewModel.IsRecordingPausedAtCurrentTime = false;
        }
    }

    // The PlaybackSession is now reused across editor opens instead of rebuilt
    // per-clip (see StartEditorPlaybackAsync) - LibVLC's VideoPlayer.Length can
    // briefly still report the PREVIOUS clip's duration for a moment after
    // LoadVideo() while it's still parsing the new file's metadata. Since this
    // runs unconditionally every playback-timer tick, a stale multi-minute/hour
    // reading (e.g. right after closing a long Full Session recording and
    // opening a short clip) could get written over the already-correct
    // ffprobe-sourced duration. Reject anything wildly different from what's
    // already known instead of blindly trusting every VLC read.
    private static bool IsPlausibleDuration(TimeSpan candidate, TimeSpan known)
    {
        if (known <= TimeSpan.Zero) return true;
        return Math.Abs((candidate - known).TotalSeconds) < 5;
    }

    // Reads the ".paused.json" sidecar NativeReplayBuffer writes next to a
    // clip when it recorded via DXGI Desktop Duplication and the game window
    // wasn't foreground for part of the recording (see class summary there).
    // Missing sidecar (Legacy/OBS backend clips, or no pauses occurred) just
    // means no badge ever shows - not an error.
    private List<(double StartSeconds, double EndSeconds)> LoadPausedRanges(string videoPath)
    {
        var sidecarPath = ViewModel is null ? string.Empty : LibraryLayout.SidecarPath(ViewModel.Settings.LibraryFolder, videoPath, ".paused.json");
        if (!File.Exists(sidecarPath))
        {
            sidecarPath = LibraryLayout.LegacySidecarPath(videoPath, ".paused.json");
            if (!File.Exists(sidecarPath)) sidecarPath = LibraryLayout.LegacyAdjacentPausedPath(videoPath);
            if (!File.Exists(sidecarPath)) return new();
        }

        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<PausedRangeEntry>>(File.ReadAllText(sidecarPath));
            return entries?.Select(e => (e.start, e.end)).ToList() ?? new();
        }
        catch (Exception error)
        {
            AppLog.Error("Failed to read recording-paused sidecar.", error);
            return new();
        }
    }

    private sealed record PausedRangeEntry(double start, double end);

    // A plain in-tree Border never actually rendered over the video because
    // LibVLCSharp's VideoView is backed by a native (non-Avalonia) hwnd
    // surface on Windows, which always paints above sibling Avalonia visuals
    // regardless of XAML z-order. A bare Avalonia Popup does get promoted to
    // a real top-level OS window to get above that surface, but Avalonia's
    // popup windows go always-on-top globally (visible over every other app,
    // and even while EVE itself is minimized) instead of being scoped to
    // EVE. An owned Window (Owner = this, no Topmost) gets normal Win32
    // owned-window z-order behavior instead: always directly above its
    // owner, hidden/minimized together with it, never floating above
    // unrelated other windows.
    private Window EnsureRecordingPausedOverlay()
    {
        if (_recordingPausedOverlay is not null) return _recordingPausedOverlay;

        var overlay = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Topmost = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)),
                Child = new TextBlock
                {
                    Text = "Recording Paused",
                    Foreground = Brushes.White,
                    FontSize = 28,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            }
        };
        _recordingPausedOverlay = overlay;
        return overlay;
    }

    private void UpdateRecordingPausedOverlay(bool shouldShow)
    {
        // Extra guard against a stale/queued timer tick reshowing this over
        // whatever's currently on screen (e.g. the library, mid-transition)
        // if it ever fires after the editor's already been left - only the
        // editor being genuinely visible right now is allowed to show it.
        if (!shouldShow || ViewModel is null || !ViewModel.IsEditorVisible)
        {
            _recordingPausedOverlay?.Hide();
            return;
        }

        var overlay = EnsureRecordingPausedOverlay();
        var topLeft = EditorVideoView.PointToScreen(new Point(0, 0));
        var bottomRight = EditorVideoView.PointToScreen(new Point(EditorVideoView.Bounds.Width, EditorVideoView.Bounds.Height));
        overlay.Position = topLeft;
        overlay.Width = Math.Max(1, (bottomRight.X - topLeft.X) / overlay.RenderScaling);
        overlay.Height = Math.Max(1, (bottomRight.Y - topLeft.Y) / overlay.RenderScaling);
        if (!overlay.IsVisible) overlay.Show(this);
    }

    private void SyncPlaybackPosition()
    {
        if (ViewModel is null || _playback is null) return;
        if (_timelineDragMode != TimelineDragMode.None) return;
        if (_playback.Duration > TimeSpan.Zero && IsPlausibleDuration(_playback.Duration, ViewModel.Duration))
        {
            ViewModel.SetDuration(_playback.Duration);
        }
        if (ViewModel.IsPlaying)
        {
            ViewModel.CurrentTime = SmoothPlaybackPosition();
        }
        else
        {
            ViewModel.CurrentTime = _playback.Position;
            SetPlayheadBase(ViewModel.CurrentTime);
            _playback.EnsurePausedIfNeeded();
        }
        UpdateTimelineChrome();
        if (_pausedRanges.Count > 0)
        {
            var currentSeconds = ViewModel.CurrentTime.TotalSeconds;
            ViewModel.IsRecordingPausedAtCurrentTime = _pausedRanges.Any(r => currentSeconds >= r.StartSeconds && currentSeconds < r.EndSeconds);
        }
        UpdateRecordingPausedOverlay(ViewModel.ShowRecordingPausedBadge);
        if (ViewModel.TrimEnd > TimeSpan.Zero && ViewModel.CurrentTime >= ViewModel.TrimEnd)
        {
            _playback.Pause();
            _ = _playback.SeekAsync(ViewModel.TrimEnd);
            ViewModel.CurrentTime = ViewModel.TrimEnd;
            SetPlayheadBase(ViewModel.CurrentTime);
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
            _endedAtTrimBoundary = true;
        }
        else if (_playback.IsEnded)
        {
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
            _endedAtTrimBoundary = true;
        }
    }

    private void UpdateTimelineFromPointer(PointerEventArgs e, TimelineDragMode mode)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        var point = e.GetPosition(TimelineSurface);
        var width = Math.Max(1, TimelineSurface.Bounds.Width);
        var time = TimeSpan.FromMilliseconds(ViewModel.Duration.TotalMilliseconds * Math.Clamp(point.X / width, 0, 1));
        switch (mode)
        {
            case TimelineDragMode.TrimStart:
                ViewModel.TrimStart = time;
                ViewModel.CurrentTime = ViewModel.TrimStart;
                ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
                break;
            case TimelineDragMode.TrimEnd:
                ViewModel.TrimEnd = time;
                ViewModel.CurrentTime = ViewModel.TrimEnd;
                ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
                break;
            case TimelineDragMode.Playhead:
                ViewModel.CurrentTime = time;
                ResetPlayheadClockAfterSeek(time);
                break;
        }

        UpdateTimelineChrome();
    }

    private async Task ApplyTimelineSeekAsync(TimeSpan time, bool resumePlayback, bool isPreview = false)
    {
        if (ViewModel is null) return;
        _editorSeekCts?.Cancel();
        _editorSeekCts?.Dispose();
        var seekCts = new CancellationTokenSource();
        _editorSeekCts = seekCts;
        _endedAtTrimBoundary = false;
        ViewModel.CurrentTime = time;
        var didResume = false;
        if (_playback is not null)
        {
            try
            {
                didResume = await _playback.SeekAsync(time, resumePlayback, seekCts.Token, isPreview);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (_editorSeekCts != seekCts) return;
        if (resumePlayback && didResume)
        {
            StartPlayheadClock(_playback?.Position ?? time);
            ViewModel.IsPlaying = true;
            _playbackTimer.Start();
        }
        else
        {
            SetPlayheadBase(_playback?.Position ?? time);
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
        }
        UpdateTimelineChrome();
    }

    private void UpdateTimelineChrome()
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        var width = Math.Max(1, TimelineSurface.Bounds.Width);
        var height = Math.Max(1, TimelineSurface.Bounds.Height);
        var start = ViewModel.TrimStart.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;
        var end = ViewModel.TrimEnd.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;
        var playhead = ViewModel.CurrentTime.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;

        Canvas.SetLeft(TrimSelection, start);
        TrimSelection.Width = Math.Max(0, end - start);

        Canvas.SetLeft(TrimStartHandle, start - TrimStartHandle.Width / 2);
        Canvas.SetTop(TrimStartHandle, 0);
        Canvas.SetLeft(TrimEndHandle, end - TrimEndHandle.Width / 2);
        Canvas.SetTop(TrimEndHandle, 0);

        Canvas.SetLeft(TimelinePlayhead, playhead - TimelinePlayhead.Width / 2);
        TimelinePlayhead.Height = height;
        Canvas.SetTop(TimelinePlayhead, -8);
        Canvas.SetLeft(PlayheadCap, playhead - 8);
        Canvas.SetTop(PlayheadCap, -12);
    }

    private void StartPlayheadClock(TimeSpan time)
    {
        _playheadBaseTime = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        _playheadClock.Restart();
    }

    private void SetPlayheadBase(TimeSpan time)
    {
        _playheadBaseTime = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        _playheadClock.Reset();
    }

    private TimeSpan SmoothPlaybackPosition()
    {
        if (ViewModel is null) return _playheadBaseTime;
        var position = _playheadBaseTime + _playheadClock.Elapsed;
        if (ViewModel.Duration > TimeSpan.Zero && position > ViewModel.Duration) return ViewModel.Duration;
        return position;
    }

    // Called from App.axaml.cs right after DataContext is assigned but before the
    // window is shown - applying saved bounds on the Opened event (after the
    // window already rendered once at its XAML-default size) caused a visible
    // flash-then-resize on every launch.
    internal void ApplySavedWindowBounds()
    {
        if (ViewModel is null) return;
        var settings = ViewModel.Settings;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        if (!double.IsNaN(settings.WindowX) && !double.IsNaN(settings.WindowY))
        {
            Position = new PixelPoint((int)settings.WindowX, (int)settings.WindowY);
        }

        if (settings.IsWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowBounds()
    {
        if (ViewModel is null) return;
        var settings = ViewModel.Settings;
        settings.IsWindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
            settings.WindowWidth = Bounds.Width;
            settings.WindowHeight = Bounds.Height;
        }
    }

    private enum TimelineDragMode
    {
        None,
        Playhead,
        TrimStart,
        TrimEnd
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null) return new ProcessResult(-1, string.Empty, "Failed to start process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static Window CreateDialog(string title, string message, bool showCancel)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brush.Parse("#111920")
        };

        var ok = new Button
        {
            Content = showCancel ? "Delete" : "OK",
            Width = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        if (showCancel)
        {
            ok.Background = Avalonia.Media.Brush.Parse("#D95B62");
            ok.Foreground = Avalonia.Media.Brush.Parse("#FFFFFF");
        }
        ok.Click += (_, _) => window.Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (showCancel)
        {
            var cancel = new Button { Content = "Cancel", Width = 96 };
            cancel.Click += (_, _) => window.Close(false);
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(ok);

        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = Avalonia.Media.Brush.Parse("#DDE8F5"),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 16
                },
                new TextBlock
                {
                    Text = message,
                    Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                buttons
            }
        };

        return window;
    }

    private static void OpenInExplorer(string path, bool selectFile)
    {
        // Process.Start with UseShellExecute=true goes through ShellExecuteEx,
        // which can genuinely block for real time (shell extension/icon
        // overlay enumeration, AV scanning the target, a cold Explorer
        // launch with no window already open) - called synchronously on the
        // UI thread this froze the whole app for however long that took.
        // Fire-and-forget on a background thread instead; there's no
        // follow-up state to update once Explorer's asked to open.
        Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    Arguments = selectFile ? $"/select,\"{path}\"" : $"\"{path}\""
                };

                Process.Start(info);
            }
            catch
            {
                // Explorer links are convenience-only.
            }
        });
    }

    private static bool IsTypingInTextInput(object? source)
    {
        return source is TextBox;
    }

    private void ApplyPrimaryCaptureBounds()
    {
        if (ViewModel is null) return;
        var primary = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (primary is null) return;
        ViewModel.ReplayCaptureX = primary.Bounds.X;
        ViewModel.ReplayCaptureY = primary.Bounds.Y;
        ViewModel.ReplayCaptureWidth = primary.Bounds.Width;
        ViewModel.ReplayCaptureHeight = primary.Bounds.Height;
    }

    private void ResetPlayheadClockAfterSeek(TimeSpan time)
    {
        if (ViewModel?.IsPlaying == true)
        {
            StartPlayheadClock(time);
        }
        else
        {
            SetPlayheadBase(time);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
