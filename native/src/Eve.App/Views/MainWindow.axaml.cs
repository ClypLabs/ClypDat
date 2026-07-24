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
    private DotaGsiListener? _dotaGsiListener;
    private LeagueAutoClipListener? _leagueAutoClipListener;
    private FortniteAutoClipListener? _fortniteAutoClipListener;
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
                _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
                ViewModel.GameCatalogChanged += (_, _) =>
                {
                    _gameDetector.ApplyCustomGameNames(ViewModel.Settings.GameCaptureOverrides);
                    _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
                };
                ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.AutoClippingEnabled)) UpdateAutoClipStates();
                    if (e.PropertyName is nameof(MainWindowViewModel.MasterVolumePercent) or nameof(MainWindowViewModel.IsMasterMuted)) _playback?.SetMasterVolume(ViewModel.EffectiveMasterVolumePercent);
                    if (e.PropertyName is nameof(MainWindowViewModel.VideoZoom) or nameof(MainWindowViewModel.VideoPanY)) UpdateVideoTransform();
                };
                foreach (var autoClipGame in ViewModel.AutoClipGames)
                {
                    autoClipGame.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(AutoClipGameViewModel.IsEnabled)) UpdateAutoClipStates();
                    };
                }
                UpdateAutoClipStates();
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
        TrackPausedOverlayToWindow();
        AddHandler(KeyUpEvent, MainWindow_OnKeyUp, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            SaveWindowBounds();
            ViewModel?.SaveSettings();
            if (!AllowRealClose)
            {
                e.Cancel = true;
                // Hiding to tray keeps the app (and replay buffer) running,
                // but PlaybackSession itself - LibVLC's video output and the
                // NAudio WasapiOut mixer - has nothing to do with the window
                // being visible. Without this, a clip actively playing in
                // the editor when the window closes just kept playing audio
                // (and technically video, decoding for nobody) indefinitely
                // in the background. A real quit already covers this via
                // _playback?.Dispose() in Closed below.
                StopEditorPlayback(stopPlaybackAsync: true);
                Hide();
                ShowInTaskbar = false;
            }
        };
        Closed += (_, _) =>
        {
            _globalHotkey?.Dispose();
            _cs2GsiListener?.Dispose();
            _dotaGsiListener?.Dispose();
            _leagueAutoClipListener?.Dispose();
            _fortniteAutoClipListener?.Dispose();
            _gameDetectionTimer.Stop();
            if (_replayBuffer is not null) _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
            _replayBuffer?.Dispose();
            _playback?.Dispose();
            _recordingPausedOverlay?.Close();
            ViewModel?.Dispose();
        };
        AddHandler(PointerPressedEvent, VolumeSlider_OnPointerPressedAny, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, VolumeSlider_OnPointerReleasedAny, RoutingStrategies.Tunnel, true);
        // Inline title editing (BeginInlineTitleEdit) needs to close/commit
        // the instant a click lands anywhere else - LostFocus alone only
        // fires when the newly clicked target is itself focusable, so a
        // click on a non-focusable area (e.g. a thumbnail, plain text) would
        // otherwise leave the box open with focus untouched.
        AddHandler(PointerPressedEvent, ClipTitleEdit_OnAnyPointerPressed, RoutingStrategies.Tunnel);
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

    // The header's detected-game text - clicking it offers "Don't detect X as
    // a game" so a wrongly-detected app (the built-in ignore list can't cover
    // everything) can be excluded on the spot instead of digging through
    // settings.
    private void ActiveGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is null) return;
        var detection = ViewModel.ActiveGameDetection;
        if (detection is not { IsDetected: true } || string.IsNullOrWhiteSpace(detection.ExeName)) return;

        var flyout = new MenuFlyout();
        var exclude = new MenuItem
        {
            Header = new TextBlock
            {
                Text = $"Don't detect \"{detection.DisplayName}\" ({detection.ExeName}) as a game",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            }
        };
        exclude.Click += (_, _) =>
        {
            ViewModel.AddIgnoredGameExecutable(detection.ExeName);
            _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
            UpdateDetectedGame();
        };
        flyout.Items.Add(exclude);
        flyout.ShowAt(button);
    }

    private void RemoveIgnoredGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string executableName } || ViewModel is null) return;
        ViewModel.RemoveIgnoredGameExecutable(executableName);
        _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
        UpdateDetectedGame();
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
        // UpdateCardLayout changes CardWidth (and possibly CardColumns),
        // which reflows the WrapPanel into different rows - the ScrollViewer's
        // own Offset stays numerically the same afterward but no longer
        // points at the same clips, since everything above it just shifted
        // to a different height. Preserving Offset as a FRACTION of the
        // total scrollable extent instead keeps roughly the same spot in the
        // library in view across the reflow, rather than the resize looking
        // like it randomly jumped somewhere else.
        var previousExtentHeight = LibraryScrollViewer.Extent.Height;
        var scrollFraction = previousExtentHeight > 0 ? LibraryScrollViewer.Offset.Y / previousExtentHeight : 0;

        ViewModel?.UpdateCardLayout(e.NewSize.Width);
        UpdateTimelineChrome();

        if (scrollFraction > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var newExtentHeight = LibraryScrollViewer.Extent.Height;
                LibraryScrollViewer.Offset = new Vector(LibraryScrollViewer.Offset.X, scrollFraction * newExtentHeight);
            });
        }
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
            // IsReplayRecording's setter is a no-op when the value doesn't change
            // (e.g. a second consecutive failed start while already false), which
            // would otherwise leave the status text frozen on stale "Replay Armed" -
            // set it directly so a failure always reflects in the UI.
            ViewModel.RecorderStatus = _replayArmed ? "Replay Armed" : "Replay Off";
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

    private async Task SaveReplayClipAsync(string? autoClipLabel = null, ReplayClipWindow? clipWindow = null, string? autoClipGameName = null)
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

                // The final four seconds belong to the event, not whatever is
                // happening when a round finishes. Wait for that tail before the
                // replay buffer snapshots its requested UTC window.
                if (clipWindow is not null)
                {
                    var wait = clipWindow.EndUtc - MonotonicClock.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    ShowClipNotification($"Saving {autoClipLabel} clip…", playSound: false);
                }

                var outputPath = await Task.Run(() => _replayBuffer.SaveReplayAsync(outputFolder, titleOverride: autoClipLabel, clipWindow: clipWindow));
                AppLog.Info($"Replay clip saved: {outputPath}");
                ShowClipSavedNotification();
                // "3K - Mirage" -> event type "3K", map dropped - the game name
                // (not the map) is what belongs next to it as the game label.
                var autoClipEventType = autoClipLabel?.Split(" - ", 2)[0];
                ClipInfoSidecar.Save(ViewModel.Settings.LibraryFolder, outputPath, new ClipInfo(
                    autoClipGameName ?? ViewModel.ActiveGameDetection.DisplayName,
                    autoClipEventType,
                    autoClipLabel ?? ViewModel.ActiveGameDetection.DisplayName,
                    File.GetCreationTimeUtc(outputPath)));
                await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
            }
            catch (Exception error)
            {
                AppLog.Error("Replay clip save failed", error);
                if (isAutoClip) ShowClipNotification("Auto clip failed", playSound: false);
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
        ShowClipNotification("Clip saved", playSound: true);
    }

    private void ShowClipNotification(string text, bool playSound)
    {
        if (ViewModel is null) return;
        if (playSound && ViewModel.Settings.EnableClipOverlaySound)
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
                ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition, text);
            }
            catch (Exception error)
            {
                AppLog.Error("Clip notification overlay failed", error);
            }
        }
    }

    private void ShowClipSavedOverlay(string position, string text)
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
                        Text = text,
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
        var configured = ViewModel.Settings.LibraryFolder;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (await Task.Run(() => Directory.Exists(configured))) return;
            // A configured folder (often a network share) that's just not reachable
            // THIS MOMENT (not yet mounted, VPN not up, drive letter not remapped)
            // must NOT fall through to creating a new local folder and overwriting
            // the setting below - that would silently redirect every future
            // recording to local disk while the user's whole existing library on
            // the share becomes invisible, with no way back short of manually
            // re-entering the path. Leave the setting alone; RefreshLibraryAsync
            // will just show an empty library until the share comes back.
            AppLog.Error($"Library folder unreachable at startup: {configured} - leaving the setting as-is instead of switching to a local default.");
            return;
        }

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
        if (e.Source is CheckBox or Button or PathIcon or TextBox) return;
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        e.Handled = true;
        await OpenClipCardAsync(clip);
    }

    private async Task<bool> OpenClipCardAsync(ClipCardViewModel clip)
    {
        if (ViewModel is null) return false;
        if (!await ViewModel.OpenClipAsync(clip)) return false;
        QueueEditorPlayback();
        return true;
    }

    private async void ClipContextExport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;
        if (!await OpenClipCardAsync(clip)) return;
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
        await RenameClipCardAsync(clip);
    }

    // At most one inline title edit is open at a time - BeginInlineTitleEdit
    // resolves whichever of these is set before starting a new one, and the
    // window-level tunnel handler (registered in the constructor) uses them
    // to close/commit the box the instant a click lands anywhere else.
    private TextBox? _activeInlineTitleEdit;
    private Action<bool>? _resolveActiveInlineTitleEdit;

    // Hovering the title (TextBlock.editableTitle's underline in
    // AppStyles.axaml) is the only affordance now - clicking it swaps it for
    // a bordered TextBox in place instead of opening a separate "type a new
    // name" dialog.
    private void ClipTitle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ClipCardViewModel clip } titleBlock) return;
        if (!e.GetCurrentPoint(titleBlock).Properties.IsLeftButtonPressed) return;
        if (titleBlock.Parent is not Panel container) return;

        e.Handled = true;
        BeginInlineTitleEdit(container, titleBlock, clip);
    }

    private void ClipTitleEdit_OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_activeInlineTitleEdit is not { } editBox) return;
        if (e.Source is Visual visual && (visual == editBox || editBox.IsVisualAncestorOf(visual))) return;
        _resolveActiveInlineTitleEdit?.Invoke(true);
    }

    private void BeginInlineTitleEdit(Panel container, TextBlock titleBlock, ClipCardViewModel clip)
    {
        // Only one at a time - starting a new one commits/closes whatever
        // was already open elsewhere in the library.
        _resolveActiveInlineTitleEdit?.Invoke(true);

        var isFileTitle = clip.IsAutoClip || clip.IsMedalImport;
        var originalText = isFileTitle ? clip.GameNameLabel : (clip.CustomTitle ?? string.Empty);

        // A plain MaxWidth here isn't enough to keep the card from growing -
        // the Panel's child is measured with unbounded available width, so
        // whatever the TextBox's OWN desired size comes out to still grows
        // the card. An explicit Width pins the TextBox's DesiredSize
        // outright regardless of content, matching the same CardWidth-minus-
        // reserve budget SubtractDoubleConverter gives the static title
        // TextBlock (see MainWindow.axaml), so the card can't inflate while
        // editing.
        var titleWidth = Math.Max(80, (ViewModel?.CardWidth ?? 220) - 32);

        var editBox = new TextBox
        {
            Text = originalText,
            // Manual clips with no CustomTitle start empty (so committing an
            // unchanged blank field stays a no-op / typing straight away
            // replaces the placeholder) - the watermark shows what's
            // actually on the card right now (clip.TileMainLabel, e.g.
            // "Clip from July 23, 2026") so the empty box doesn't look blank
            // for no reason.
            Watermark = clip.TileMainLabel,
            Classes = { "inlineTitleEdit" },
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            // Fluent's TextBox defaults to a much taller MinHeight (~32px)
            // than a plain 15px Bold TextBlock's own line height - without
            // pinning this down explicitly, swapping the two made the whole
            // card visibly jump/reflow (title row growing ~12px taller, the
            // date/duration row below shoved down) the instant editing
            // started, then snapping back when it ended.
            MinHeight = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = titleWidth
        };

        titleBlock.IsVisible = false;
        container.Children.Add(editBox);
        _activeInlineTitleEdit = editBox;

        Dispatcher.UIThread.Post(() =>
        {
            editBox.Focus();
            editBox.SelectAll();
        });

        // Enter/blur/click-elsewhere all commit, Escape cancels - guarded by
        // resolved so removing the box (which can itself trigger a blur)
        // can't re-fire and commit a second time.
        var resolved = false;

        async void Resolve(bool save)
        {
            if (resolved) return;
            resolved = true;

            container.Children.Remove(editBox);
            titleBlock.IsVisible = true;
            _activeInlineTitleEdit = null;
            _resolveActiveInlineTitleEdit = null;

            if (!save) return;
            var newTitle = (editBox.Text ?? string.Empty).Trim();
            if (isFileTitle && string.IsNullOrWhiteSpace(newTitle)) return;
            if (newTitle == originalText) return;

            await ApplyClipTitleRenameAsync(clip, newTitle);
        }

        _resolveActiveInlineTitleEdit = Resolve;

        editBox.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Enter) { keyArgs.Handled = true; Resolve(true); }
            else if (keyArgs.Key == Key.Escape) { keyArgs.Handled = true; Resolve(false); }
        };
        editBox.LostFocus += (_, _) => Resolve(true);
    }

    private async Task RenameClipCardAsync(ClipCardViewModel clip)
    {
        if (ViewModel is null) return;

        // Auto-clips (CS2 kill clips etc.) and Medal imports both show their
        // filename-derived title ("<event> - <map>" / the imported clip's own
        // title) as the main tile label, so renaming that IS the clip's
        // title - it has to go through RenameClipAsync to actually change
        // the game-name portion of the file on disk (leaving the trailing
        // date/time suffix untouched). Everything else (manual clips, VODs)
        // shows "Clip from {date}" as a placeholder there instead - rename
        // that card's own custom label, not a title baked into the filename.
        var isFileTitle = clip.IsAutoClip || clip.IsMedalImport;
        var currentTitle = isFileTitle ? clip.GameNameLabel : (clip.CustomTitle ?? string.Empty);

        var newTitle = await PromptRenameAsync(currentTitle);
        if (newTitle is null) return;
        var trimmed = newTitle.Trim();
        if (trimmed == currentTitle) return;

        await ApplyClipTitleRenameAsync(clip, trimmed);
    }

    private async Task ApplyClipTitleRenameAsync(ClipCardViewModel clip, string newTitle)
    {
        if (ViewModel is null) return;

        var isFileTitle = clip.IsAutoClip || clip.IsMedalImport;
        if (isFileTitle && string.IsNullOrWhiteSpace(newTitle)) return;

        try
        {
            if (isFileTitle) await ViewModel.RenameClipAsync(clip, newTitle);
            else await ViewModel.RenameClipTitleAsync(clip, newTitle);
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Rename failed", error.Message);
        }
    }

    // Enter in the editor's own Title field renames the clip the same way
    // Library's pencil/inline-edit does, instead of the field only ever
    // being read later as an export filename suggestion.
    private async void EditorTitle_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null) return;
        e.Handled = true;

        var clip = ViewModel.AllClips.FirstOrDefault(c => string.Equals(c.Path, ViewModel.SelectedVideoPath, StringComparison.OrdinalIgnoreCase));
        if (clip is null) return;

        // EditorTitle defaults to the clip's filename stem, which already
        // ends in a date/time suffix (see the export flow's identical strip
        // below) - pass that through unstripped and RenameClipAsync would
        // treat the WHOLE thing as the title, doubling the timestamp onto
        // the renamed file.
        var newTitle = ClipFileNaming.StripTimestampSuffix(ViewModel.EditorTitle).Trim();
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        await ApplyClipTitleRenameAsync(clip, newTitle);
    }

    private void ClipContextOpenLocation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip }) return;
        ExplorerService.Open(clip.Path, selectFile: true);
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

    // Fires as each card's own row scrolls in/out of the library
    // ScrollViewer's clipped viewport (also on initial layout, so anything
    // below the fold starts out reporting an empty viewport) - lets
    // ClipCardViewModel decode/dispose its thumbnail Bitmap lazily instead
    // of every card in the library holding a decoded bitmap at once.
    private void ClipCard_OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Control { DataContext: ClipCardViewModel clip }) return;
        var viewport = e.EffectiveViewport;
        clip.SetPreviewVisible(viewport.Width > 0 && viewport.Height > 0);
    }

    private void ClipCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ClipCardViewModel clip, IsChecked: var isChecked } && ViewModel is not null)
        {
            ViewModel.SetClipSelected(clip, isChecked == true);
        }
    }

    private void ClipDayCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ClipCardViewModel clip, IsChecked: var isChecked } && ViewModel is not null)
        {
            ViewModel.ToggleDaySelection(clip, isChecked == true);
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
        if (ViewModel?.IsVideoFullscreen == true)
        {
            ExitVideoFullscreen();
            return;
        }

        _preFullscreenWindowState = WindowState;
        WindowState = WindowState.FullScreen;
        ViewModel?.SetVideoFullscreen(true);

        // Move the SAME EditorVideoView (already playing) into the
        // fullscreen host instead of hot-swapping MediaPlayer onto a second
        // VideoView - that never actually rendered a frame into the new
        // surface (tried twice, confirmed via logs the swap ran but stayed
        // black). The control's MediaPlayer is never touched here.
        EditorVideoHost.Children.Remove(EditorVideoView);
        FullscreenVideoHost.Children.Add(EditorVideoView);
        AppLog.Info("Video fullscreen entered: EditorVideoView reparented into FullscreenVideoHost.");
    }

    private void ExitVideoFullscreenButton_OnClick(object? sender, RoutedEventArgs e) => ExitVideoFullscreen();

    // Scroll up = zoom in, scroll down = zoom out - wired to both
    // EditorVideoHost and FullscreenVideoHost (same handler, same
    // ViewModel.VideoZoom either way, since it's the same EditorVideoView
    // reparented between them).
    private void VideoHost_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        if (e.Delta.Y == 0) return;
        const double zoomStep = 0.25;
        ViewModel.VideoZoom += e.Delta.Y > 0 ? zoomStep : -zoomStep;
        e.Handled = true;
    }

    // Turns VideoZoom/VideoPanY (both normalized ViewModel values with no
    // notion of pixels) into the actual RenderTransform on EditorVideoView -
    // needs its current rendered size, which only code-behind has, so this
    // can't just be a plain XAML binding. Called on every VideoZoom/VideoPanY
    // change and on EditorVideoView's own LayoutUpdated (covers window
    // resize and the fullscreen reparent, both of which change the height
    // the pan range is computed from).
    private void UpdateVideoTransform()
    {
        if (ViewModel is null) return;
        // x:Name on a Transform nested inside a TransformGroup property
        // element doesn't generate a code-behind field the way a named
        // Control does - looked up by index instead (matches XAML order:
        // ScaleTransform then TranslateTransform).
        if (EditorVideoView.RenderTransform is not TransformGroup group) return;
        if (group.Children is not [ScaleTransform scale, TranslateTransform translate]) return;

        scale.ScaleX = ViewModel.VideoZoom;
        scale.ScaleY = ViewModel.VideoZoom;

        var height = EditorVideoView.Bounds.Height;
        var maxPanPixels = height * (ViewModel.VideoZoom - 1) / 2;
        translate.Y = ViewModel.VideoPanY * maxPanPixels;
    }

    private void ExitVideoFullscreen()
    {
        WindowState = _preFullscreenWindowState;
        ViewModel?.SetVideoFullscreen(false);

        FullscreenVideoHost.Children.Remove(EditorVideoView);
        EditorVideoHost.Children.Insert(0, EditorVideoView);
        AppLog.Info("Video fullscreen exited: EditorVideoView reparented back into EditorVideoHost.");
    }

    private void FullscreenProgressBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || sender is not Control control || ViewModel.Duration <= TimeSpan.Zero) return;
        var wasPlaying = ViewModel.IsPlaying;
        var fraction = Math.Clamp(e.GetPosition(control).X / Math.Max(1, control.Bounds.Width), 0, 1);
        ViewModel.CurrentTime = TimeSpan.FromMilliseconds(ViewModel.Duration.TotalMilliseconds * fraction);
        ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
        _ = ApplyTimelineSeekAsync(ViewModel.CurrentTime, wasPlaying);
        e.Handled = true;
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

    private void ToggleMedalImportSelection_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleMedalImportSelection();
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

    private void RemoveGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameBackendRowViewModel row } && ViewModel is not null)
        {
            ViewModel.RemoveGame(row);
        }
    }

    private void UpdateAutoClipStates()
    {
        if (ViewModel is null) return;
        UpdateCs2AutoClipState();
        UpdateDotaAutoClipState();
        UpdateLeagueAutoClipState();
        UpdateFortniteAutoClipState();
    }

    private void UpdateCs2AutoClipState()
    {
        if (ViewModel is null) return;
        var game = ViewModel.FindAutoClipGame("cs2");
        if (game is null) return;

        if (!ViewModel.AutoClippingEnabled || !game.IsEnabled)
        {
            if (_cs2GsiListener is not null)
            {
                _cs2GsiListener.AutoClipPending -= Cs2GsiListener_OnAutoClipPending;
                _cs2GsiListener.AutoClipReady -= Cs2GsiListener_OnAutoClipReady;
                _cs2GsiListener.Stop();
            }

            game.StatusText = "Disabled";
            return;
        }

        _cs2GsiListener ??= new Cs2GsiListener(() => ViewModel.Settings.AutoClipping.Games["cs2"]);
        if (_cs2GsiListener.IsListening) return;

        var port = ViewModel.Settings.AutoClipping.Games["cs2"].ListenerPort;
        if (!_cs2GsiListener.Start(port))
        {
            game.StatusText = $"Listener couldn't start on port {port} - it may already be in use.";
            return;
        }

        _cs2GsiListener.AutoClipPending += Cs2GsiListener_OnAutoClipPending;
        _cs2GsiListener.AutoClipReady += Cs2GsiListener_OnAutoClipReady;
        Cs2GsiDeployer.TryDeploy(port, out var statusMessage);
        game.StatusText = statusMessage;
    }

    private void UpdateDotaAutoClipState()
    {
        if (ViewModel is null) return;
        var game = ViewModel.FindAutoClipGame("dota2"); if (game is null) return;
        if (!ViewModel.AutoClippingEnabled || !game.IsEnabled)
        {
            if (_dotaGsiListener is not null) { _dotaGsiListener.AutoClipPending -= AutoClip_OnPending; _dotaGsiListener.AutoClipReady -= AutoClip_OnReady; _dotaGsiListener.Stop(); }
            game.StatusText = "Disabled"; return;
        }
        _dotaGsiListener ??= new DotaGsiListener(() => ViewModel.Settings.AutoClipping.Games["dota2"]);
        if (!_dotaGsiListener.IsListening)
        {
            var port = ViewModel.Settings.AutoClipping.Games["dota2"].ListenerPort;
            if (!_dotaGsiListener.Start(port)) { game.StatusText = $"Listener couldn't start on port {port}."; return; }
            _dotaGsiListener.AutoClipPending += AutoClip_OnPending; _dotaGsiListener.AutoClipReady += AutoClip_OnReady;
        }
        DotaGsiDeployer.TryDeploy(ViewModel.Settings.AutoClipping.Games["dota2"].ListenerPort, out var status); game.StatusText = status;
    }

    private void UpdateLeagueAutoClipState()
    {
        if (ViewModel is null) return;
        var game = ViewModel.FindAutoClipGame("league"); if (game is null) return;
        if (!ViewModel.AutoClippingEnabled || !game.IsEnabled)
        {
            _leagueAutoClipListener?.Stop(); game.StatusText = "Disabled"; return;
        }
        _leagueAutoClipListener ??= new LeagueAutoClipListener(() => ViewModel.Settings.AutoClipping.Games["league"]);
        _leagueAutoClipListener.AutoClipPending -= AutoClip_OnPending; _leagueAutoClipListener.AutoClipReady -= AutoClip_OnReady;
        _leagueAutoClipListener.AutoClipPending += AutoClip_OnPending; _leagueAutoClipListener.AutoClipReady += AutoClip_OnReady;
        _leagueAutoClipListener.Start(); game.StatusText = "Waiting for a live League match";
    }

    private void UpdateFortniteAutoClipState()
    {
        if (ViewModel is null) return;
        var game = ViewModel.FindAutoClipGame("fortnite"); if (game is null) return;
        if (!ViewModel.AutoClippingEnabled || !game.IsEnabled)
        {
            if (_fortniteAutoClipListener is not null)
            {
                _fortniteAutoClipListener.AutoClipPending -= AutoClip_OnPending;
                _fortniteAutoClipListener.AutoClipReady -= AutoClip_OnReady;
                _fortniteAutoClipListener.StatusChanged -= FortniteAutoClipListener_OnStatusChanged;
                _fortniteAutoClipListener.Stop();
            }
            game.StatusText = "Disabled";
            return;
        }

        _fortniteAutoClipListener ??= new FortniteAutoClipListener(() => ViewModel.Settings.AutoClipping.Games["fortnite"]);
        _fortniteAutoClipListener.AutoClipPending -= AutoClip_OnPending;
        _fortniteAutoClipListener.AutoClipReady -= AutoClip_OnReady;
        _fortniteAutoClipListener.StatusChanged -= FortniteAutoClipListener_OnStatusChanged;
        _fortniteAutoClipListener.AutoClipPending += AutoClip_OnPending;
        _fortniteAutoClipListener.AutoClipReady += AutoClip_OnReady;
        _fortniteAutoClipListener.StatusChanged += FortniteAutoClipListener_OnStatusChanged;
        _fortniteAutoClipListener.Start();
        game.StatusText = _fortniteAutoClipListener.StatusText;
    }

    private void FortniteAutoClipListener_OnStatusChanged(object? sender, string status) => Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel?.FindAutoClipGame("fortnite") is { } game) game.StatusText = status;
    });

    private void Cs2GsiListener_OnAutoClipPending(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() => ShowClipNotification(message, playSound: false));
    }

    private void Cs2GsiListener_OnAutoClipReady(object? sender, Cs2AutoClipRequest request)
    {
        AutoClip_OnReady(sender, new AutoClipRequest("cs2", "Counter-Strike 2", request.Title, request.Title, request.StartUtc, request.EndUtc));
    }

    private void AutoClip_OnPending(object? sender, string message) => Dispatcher.UIThread.Post(() => ShowClipNotification(message, playSound: false));

    private void AutoClip_OnReady(object? sender, AutoClipRequest request)
    {
        Dispatcher.UIThread.Post(() => _ = SaveReplayClipAsync(request.Title, new ReplayClipWindow(request.StartUtc, request.EndUtc), request.GameName));
    }

    private void SetupDotaAutoClipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var port = ViewModel.Settings.AutoClipping.Games["dota2"].ListenerPort;
        DotaGsiDeployer.TryDeploy(port, out var status);
        if (ViewModel.FindAutoClipGame("dota2") is { } game) game.StatusText = status;
    }

    private void AutoClipGroupToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AutoClipGroupViewModel group }) group.Toggle();
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
        ExplorerService.Open(ViewModel.Settings.LibraryFolder, selectFile: false);
    }

    private void EditorPathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        ExplorerService.Open(ViewModel.SelectedVideoPath, selectFile: true);
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
            if (ViewModel.IsVideoFullscreen)
            {
                ExitVideoFullscreen();
                e.Handled = true;
                return;
            }

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
            // Warm the audio chunk cache at both trim markers - the very next
            // actions after placing a handle are usually jumping to it
            // (Restart plays from TrimStart, End jumps near TrimEnd), and on
            // a network drive the extraction round-trip is what a user would
            // otherwise hear as a silent beat after that jump.
            _playback?.PrefetchAudioAt(ViewModel.TrimStart);
            if (ViewModel.TrimEnd > TimeSpan.Zero) _playback?.PrefetchAudioAt(ViewModel.TrimEnd);
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

    // Same toggle-and-remember pattern as TrackMuteToggle_OnPointerPressed,
    // for the master volume icon (present in both the regular editor and
    // fullscreen playbars - same handler, same ViewModel property, either
    // one it's clicked from).
    private void MasterVolumeMuteToggle_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.IsMasterMuted = !ViewModel.IsMasterMuted;
        e.Handled = true;
    }

    private void TrackVolumeReset_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackLaneViewModel track }) return;
        track.VolumePercent = 100;
        _playback?.SetTrackVolume(track.StreamIndex, track.EffectiveVolumePercent);
        ViewModel?.SaveSelectedClipEditState();
    }

    private void MasterVolumeReset_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.MasterVolumePercent = 100;
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

    private async void SaveTrimButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveTrimToOriginalAsync();
    }

    private async Task SaveTrimToOriginalAsync()
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        var sourcePath = ViewModel.SelectedVideoPath;

        var trimEnd = ViewModel.TrimEnd > ViewModel.TrimStart ? ViewModel.TrimEnd : ViewModel.Duration;
        var hasTrim = ViewModel.TrimStart > TimeSpan.FromMilliseconds(50) || trimEnd < ViewModel.Duration - TimeSpan.FromMilliseconds(50);
        if (!hasTrim)
        {
            await ShowMessageAsync("Nothing to trim", "Drag the trim handles on the timeline first, then Save Trim.");
            return;
        }

        var dialog = CreateDialog(
            "Save trim?",
            "This replaces the original clip with just the trimmed range. This can't be undone.",
            showCancel: true,
            confirmLabel: "Save Trim",
            destructive: false);
        if (!await dialog.ShowDialog<bool>(this)) return;

        var tempPath = Path.Combine(Path.GetTempPath(), $"eve-save-trim-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");

        ViewModel.IsExporting = true;
        var progressCts = new CancellationTokenSource();
        var (progressWindow, progressBar, statusText, percentText, etaText) = CreateProgressDialog("Saving trim", "Saving trim...", () => progressCts.Cancel());
        var progressDialogTask = progressWindow.ShowDialog(this);
        try
        {
            var exportDuration = ViewModel.ExportDuration;
            var encodeClock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(fraction =>
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(fraction * 100, 0, 100);
                percentText.Text = $"{progressBar.Value:0}%";
                if (fraction > 0.03)
                {
                    var remaining = TimeSpan.FromMilliseconds(encodeClock.ElapsedMilliseconds * (1 - fraction) / fraction);
                    etaText.Text = $"Estimated: {FormatEta(remaining)}";
                    etaText.IsVisible = true;
                }
            });
            var result = await RunProcessWithProgressAsync("ffmpeg", ViewModel.BuildTrimArguments(tempPath), exportDuration, progress, progressCts.Token);
            if (result.ExitCode != 0 && !progressCts.IsCancellationRequested)
            {
                // Same NVENC-then-CPU fallback as Export.
                AppLog.Info($"Save Trim: NVENC encode failed, retrying with CPU encoder. ffmpeg said: {result.Error}");
                progressBar.IsIndeterminate = true;
                statusText.Text = "Saving trim (CPU encoder)...";
                percentText.Text = string.Empty;
                etaText.IsVisible = false;
                encodeClock.Restart();
                result = await RunProcessWithProgressAsync("ffmpeg", ViewModel.BuildTrimArguments(tempPath, useHardwareEncoder: false), exportDuration, progress, progressCts.Token);
            }
            progressWindow.Close();
            if (progressCts.IsCancellationRequested) return;
            if (result.ExitCode != 0)
            {
                await ShowMessageAsync("Save Trim failed", string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed." : result.Error);
                return;
            }

            // Release EditorVideoView's hold on the source file before replacing
            // it - libvlc keeps an open handle on the currently loaded clip,
            // which would otherwise make the File.Move calls below fail with a
            // sharing violation. Synchronous variant: the moves right after need
            // the handle actually gone, not just releasing eventually.
            StopEditorPlayback();

            var createdUtc = File.GetCreationTimeUtc(sourcePath);
            var backupPath = sourcePath + ".eve-trim-backup";
            AudioCapturePipeline.TryDelete(backupPath);
            File.Move(sourcePath, backupPath);
            try
            {
                File.Move(tempPath, sourcePath);
            }
            catch
            {
                // Restore the original instead of leaving the clip missing.
                File.Move(backupPath, sourcePath);
                throw;
            }
            File.SetCreationTimeUtc(sourcePath, createdUtc);
            AudioCapturePipeline.TryDelete(backupPath);

            await ViewModel.FinalizeSavedTrimAsync(sourcePath);
            QueueEditorPlayback();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Save Trim failed", error.Message);
        }
        finally
        {
            if (progressWindow.IsVisible) progressWindow.Close();
            await progressDialogTask;
            progressCts.Dispose();
            AudioCapturePipeline.TryDelete(tempPath);
            ViewModel.IsExporting = false;
        }
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
        // The clip's actual recording date (matches "Created:" in the editor's
        // top bar), not whenever Export happened to be clicked - using
        // DateTime.Now here produced filenames with today's date tacked onto
        // an already date-stamped title, e.g. "Marvel Rivals - Jul-11-2026 -
        // 22-54-55 - Jul-17-2026 - 05-20-02.mp4".
        var exportTimestamp = ViewModel.SelectedCreatedAtLocal > default(DateTime) ? ViewModel.SelectedCreatedAtLocal : DateTime.Now;
        // EditorTitle defaults to the clip's filename stem, which already ends
        // in the date/time this naming scheme appended when the clip was saved -
        // running that through BuildFileName unchanged appends a SECOND
        // timestamp ("Fortnite - Jul-14-2026 - 01-17-03 - Jul-14-2026 -
        // 01-17-03.mp4"). Strip the existing suffix off first; the scheme adds
        // the (correct, recording-date) one back.
        var exportTitle = ClipFileNaming.StripTimestampSuffix(ViewModel.EditorTitle);
        var suggestedFileName = ClipFileNaming.BuildFileName(
            exportTitle,
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
        var progressCts = new CancellationTokenSource();
        var (progressWindow, progressBar, statusText, percentText, etaText) = CreateProgressDialog("Exporting clip", "Exporting clip...", () => progressCts.Cancel());
        var progressDialogTask = progressWindow.ShowDialog(this);
        try
        {
            _playback?.Pause();
            ViewModel.IsPlaying = false;
            var exportDuration = ViewModel.ExportDuration;
            var encodeClock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(fraction =>
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(fraction * 100, 0, 100);
                percentText.Text = $"{progressBar.Value:0}%";
                // Simple elapsed/fraction extrapolation; below a few percent
                // one early sample would wildly overshoot, so hold off.
                if (fraction > 0.03)
                {
                    var remaining = TimeSpan.FromMilliseconds(encodeClock.ElapsedMilliseconds * (1 - fraction) / fraction);
                    etaText.Text = $"Estimated: {FormatEta(remaining)}";
                    etaText.IsVisible = true;
                }
            });
            var result = await RunProcessWithProgressAsync("ffmpeg", ViewModel.BuildExportArguments(outputPath), exportDuration, progress, progressCts.Token);
            if (result.ExitCode != 0 && !progressCts.IsCancellationRequested)
            {
                // NVENC encode failed (no NVIDIA GPU, driver too old) - redo
                // the whole encode on the CPU instead of surfacing an error.
                AppLog.Info($"Export: NVENC encode failed, retrying with CPU encoder. ffmpeg said: {result.Error}");
                progressBar.IsIndeterminate = true;
                statusText.Text = "Exporting clip (CPU encoder)...";
                percentText.Text = string.Empty;
                etaText.IsVisible = false;
                encodeClock.Restart();
                result = await RunProcessWithProgressAsync("ffmpeg", ViewModel.BuildExportArguments(outputPath, useHardwareEncoder: false), exportDuration, progress, progressCts.Token);
            }
            progressWindow.Close();
            if (progressCts.IsCancellationRequested)
            {
                AudioCapturePipeline.TryDelete(outputPath);
            }
            else if (result.ExitCode != 0)
            {
                await ShowMessageAsync("Export failed", string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed." : result.Error);
            }
            else
            {
                // FileTitle stays the game (not exportTitle) so the tile's top
                // label survives a rename - previously exportTitle went into
                // FileTitle directly, so typing a custom title in the editor
                // silently overwrote the game association on the exported
                // card. CustomTitle only gets exportTitle when it's actually
                // different from the game (the user typed something), so an
                // untouched title falls back to ClipFromLabel's "Exported clip
                // from" text instead of showing the game name twice.
                var isCustomTitle = !string.Equals(exportTitle, game, StringComparison.OrdinalIgnoreCase);
                ClipInfoSidecar.Save(libraryRoot, outputPath, new ClipInfo(
                    GameDisplayName: game,
                    AutoClipEventType: null,
                    FileTitle: game,
                    CapturedAt: exportTimestamp,
                    CustomTitle: isCustomTitle ? exportTitle : null,
                    IsExport: true));
                if (IsPathWithinLibrary(outputPath, libraryRoot)) await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
                ExplorerService.Open(outputPath, selectFile: true);
            }
        }
        finally
        {
            if (progressWindow.IsVisible) progressWindow.Close();
            await progressDialogTask;
            progressCts.Dispose();
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
        var (window, body) = CreateChromelessDialog("Rename clip");

        var textBox = new TextBox
        {
            Text = currentTitle,
            Watermark = "Clip title"
        };

        var rename = new Button
        {
            Content = "Rename",
            Width = 100,
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primaryButton" }
        };
        var cancel = new Button { Content = "Cancel", Width = 100, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };

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

        body.Children.Add(new TextBlock
        {
            Text = "Rename clip",
            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 18
        });
        body.Children.Add(textBox);
        body.Children.Add(buttons);

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
        // 2px down: TextBlock's cap height sits visually higher than the icon's
        // optical center at this size, reading as misaligned despite both being
        // VerticalAlignment=Center.
        var titleText = new TextBlock { Text = "Update available", Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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

        var updateButton = new Button { Name = "UpdateNowButton", Content = "Update Now", Width = 120, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primaryButton" } };
        var laterButton = new Button { Content = "Remind Me Later", Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var ignoreButton = new Button { Content = "Skip This Version", HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };

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
                // The update helper Wait-Process-es for THIS process to exit
                // before swapping files and relaunching - and since close-to-
                // tray shipped, closing windows no longer exits the process,
                // so the helper waited forever and the app never restarted.
                // Exit for real, exactly like the tray's own Quit item.
                AllowRealClose = true;
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Environment.Exit(0);
                }
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
            Margin = new Avalonia.Thickness(22, 20, 22, 16),
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
                notesPanel
            }
        };

        // Status/progress/buttons live in a fixed footer outside the
        // ScrollViewer - previously they were the last children of the same
        // scrolled `body`, so a long release-notes list pushed Update Now/
        // Later/Skip below the fold and the user had to scroll to reach them.
        var footer = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 0, 22, 20),
            Spacing = 10,
            Children =
            {
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

        var notesScroll = new ScrollViewer { Content = body, MaxHeight = 440 };

        window.Content = new DockPanel
        {
            Children =
            {
                titleBar,
                footer,
                notesScroll
            }
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);

        return window;
    }

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private void QueueEditorPlayback()
    {
        _playbackStartCts?.Cancel();
        _playbackStartCts?.Dispose();
        var cts = new CancellationTokenSource();
        _playbackStartCts = cts;

        // UpdateTimelineChrome only otherwise runs from a resize handler or from
        // deep inside StartEditorPlaybackAsync (after the video's first decoded
        // frame, plus its own 200ms settle delay) - opening a new clip already
        // sets fresh Duration/TrimStart/TrimEnd/CurrentTime on the ViewModel
        // synchronously (see OpenMedia), but without this the trim handles/
        // seeker stayed at the PREVIOUS clip's pixel positions on screen until
        // one of those later triggers finally caught up, which read as a
        // stuck/laggy timeline. Snap it to the new clip's values immediately.
        UpdateTimelineChrome();

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
            playback.SetMasterVolume(ViewModel.EffectiveMasterVolumePercent);
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
            var videoReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrameClock = System.Diagnostics.Stopwatch.StartNew();
            void OnTimeChanged(object? _, MediaPlayerTimeChangedEventArgs __)
            {
                playback.VideoPlayer.TimeChanged -= OnTimeChanged;
                videoReady.TrySetResult();
                // Time from play request to first decoded frame - the primary
                // "how slow is this clip's storage" number for network-drive
                // diagnosis (pairs with the "Editor video load: network=..."
                // line logged at LoadVideo).
                AppLog.Debug($"Editor first frame after {firstFrameClock.ElapsedMilliseconds}ms.");
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (ViewModel is null) return;
                    ViewModel.IsEditorVideoLoading = false;
                    // The playhead/timeline seeker previously started moving
                    // the instant PlayFrom was called, well before the video
                    // itself had a real frame to show - the seeker visibly
                    // crept forward over a still-"Loading" placeholder.
                    // Starting it here instead, atomically with clearing the
                    // loading flag, means nothing on the timeline moves
                    // until there's an actual frame for it to correspond to.
                    StartPlayheadClock(ViewModel.CurrentTime);
                    _endedAtTrimBoundary = false;
                    ViewModel.IsPlaying = true;
                    _playbackTimer.Start();
                });
            }
            playback.VideoPlayer.TimeChanged += OnTimeChanged;

            playback.PlayFrom(ViewModel.CurrentTime);
            _ = LoadEditorAudioAsync(playback, ViewModel.SelectedVideoPath, audioTracks, videoReady.Task, cancellationToken);
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
        Task videoReady,
        CancellationToken cancellationToken)
    {
        try
        {
            await playback.LoadAudioAsync(videoPath, audioTracks, ViewModel?.Duration ?? TimeSpan.Zero, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _playback != playback) return;
            // Warm the chunk cache at the clip's saved trim markers too -
            // jumping straight to a previously-set trim point is a common
            // first action after opening a clip.
            if (ViewModel is { } viewModel)
            {
                if (viewModel.TrimStart > TimeSpan.Zero) playback.PrefetchAudioAt(viewModel.TrimStart);
                if (viewModel.TrimEnd > TimeSpan.Zero && viewModel.TrimEnd < viewModel.Duration) playback.PrefetchAudioAt(viewModel.TrimEnd);
            }
            // Don't let audio start before the video's first real frame is
            // actually visible (the same TimeChanged confirmation that
            // clears IsEditorVideoLoading) - otherwise a clip that's slow to
            // open plays audio-only while the "Loading" placeholder is still
            // showing, which sounds like it's running ahead of a black
            // screen. Already-completed by the time this runs (the common
            // case, since video usually confirms before audio extraction
            // finishes) resolves immediately, no extra delay.
            await videoReady.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    Text = "Playback Paused",
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
        RepositionPausedOverlay(overlay);
        if (!overlay.IsVisible) overlay.Show(this);
    }

    private void RepositionPausedOverlay(Window overlay)
    {
        var topLeft = EditorVideoView.PointToScreen(new Point(0, 0));
        var bottomRight = EditorVideoView.PointToScreen(new Point(EditorVideoView.Bounds.Width, EditorVideoView.Bounds.Height));
        overlay.Position = topLeft;
        overlay.Width = Math.Max(1, (bottomRight.X - topLeft.X) / overlay.RenderScaling);
        overlay.Height = Math.Max(1, (bottomRight.Y - topLeft.Y) / overlay.RenderScaling);
    }

    // Keeps the badge glued to the video area during window drags/resizes -
    // without this its position only updated on playback-timer ticks (and
    // not at all while paused), so it visibly lagged/snapped behind the
    // window instead of moving with it.
    private void TrackPausedOverlayToWindow()
    {
        PositionChanged += (_, _) =>
        {
            if (_recordingPausedOverlay is { IsVisible: true } overlay) RepositionPausedOverlay(overlay);
        };
        EditorVideoView.LayoutUpdated += (_, _) =>
        {
            if (_recordingPausedOverlay is { IsVisible: true } overlay) RepositionPausedOverlay(overlay);
            // Covers window resize AND the fullscreen reparent (both change
            // EditorVideoView's rendered height, which the pan-range math
            // depends on) without needing separate handlers for each.
            UpdateVideoTransform();
        };
    }

    // Recomputes the "Playback Paused" badge for the CURRENT position. Must
    // run on every path that moves/settles the playhead - it used to live
    // only inside the playback-timer tick, so with playback paused (timer
    // stopped) a seek that landed inside a frozen range never showed the
    // badge until the user pressed play.
    private void RefreshPausedBadge()
    {
        if (ViewModel is null) return;
        if (_pausedRanges.Count > 0)
        {
            var currentSeconds = ViewModel.CurrentTime.TotalSeconds;
            ViewModel.IsRecordingPausedAtCurrentTime = _pausedRanges.Any(r => currentSeconds >= r.StartSeconds && currentSeconds < r.EndSeconds);
        }
        UpdateRecordingPausedOverlay(ViewModel.ShowRecordingPausedBadge);
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
        RefreshPausedBadge();
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
        // Timer may be stopped here (seek while paused) - refresh the frozen-
        // range badge for the landing position explicitly.
        RefreshPausedBadge();
    }

    private void UpdateTimelineChrome()
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        var width = Math.Max(1, TimelineSurface.Bounds.Width);
        var height = Math.Max(1, TimelineSurface.Bounds.Height);
        var start = ViewModel.TrimStart.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;
        var end = ViewModel.TrimEnd.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;
        var playhead = ViewModel.CurrentTime.TotalMilliseconds / ViewModel.Duration.TotalMilliseconds * width;

        // Thin lines confined to just the video lane (TrackLaneViewModel's
        // video LaneHeight), not the full track stack - matches
        // TrimSelection's old scope, just restyled.
        const double videoLaneHeight = 42;
        // Matches TrimStartCap/TrimEndCap's Points width (see XAML) - read as
        // a constant rather than Bounds.Width since the Polygon may not have
        // been measured yet on the very first call.
        const double capWidth = 10;

        // Sits entirely on the excluded side of the boundary (flush against
        // it, not centered on it) - "thicker" toward the left for the start
        // handle and toward the right for the end handle, like a bracket
        // hugging the selected range from outside instead of overlapping it.
        var startMaxLeft = Math.Max(0, width - TrimStartHandle.Width);
        var startLeft = Math.Clamp(start - TrimStartHandle.Width, 0, startMaxLeft);
        Canvas.SetLeft(TrimStartHandle, startLeft);
        Canvas.SetTop(TrimStartHandle, 0);
        TrimStartHandle.Height = videoLaneHeight;
        Canvas.SetLeft(TrimStartCap, startLeft - (capWidth - TrimStartHandle.Width) / 2);
        Canvas.SetTop(TrimStartCap, -7);

        var endMaxLeft = Math.Max(0, width - TrimEndHandle.Width);
        var endLeft = Math.Clamp(end, 0, endMaxLeft);
        Canvas.SetLeft(TrimEndHandle, endLeft);
        Canvas.SetTop(TrimEndHandle, 0);
        TrimEndHandle.Height = videoLaneHeight;
        Canvas.SetLeft(TrimEndCap, endLeft - (capWidth - TrimEndHandle.Width) / 2);
        Canvas.SetTop(TrimEndCap, -7);

        // Clamped the same way the handles are - uncentered, it could
        // otherwise poke a sliver out past the timeline's left edge at
        // CurrentTime=0, visible peeking out from behind TrimStartHandle.
        var playheadMaxLeft = Math.Max(0, width - TimelinePlayhead.Width);
        Canvas.SetLeft(TimelinePlayhead, Math.Clamp(playhead - TimelinePlayhead.Width / 2, 0, playheadMaxLeft));
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

    // Same as RunProcessAsync, but built for ffmpeg specifically - reads stdout
    // line by line instead of buffering it all, watching for the "-progress
    // pipe:1" key=value lines BuildExportArguments already asks ffmpeg to emit
    // (one "out_time_us=<microseconds>" per encoded chunk) to report real
    // percentage progress back to the caller instead of an indefinite spinner.
    private static async Task<ProcessResult> RunProcessWithProgressAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan totalDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
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

        var errorTask = process.StandardError.ReadToEndAsync();
        var outputBuilder = new System.Text.StringBuilder();

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                outputBuilder.AppendLine(line);
                if (progress is not null && totalDuration > TimeSpan.Zero && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds))
                {
                    progress.Report(Math.Clamp(microseconds / 1000.0 / totalDuration.TotalMilliseconds, 0, 1));
                }
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new ProcessResult(-1, outputBuilder.ToString(), "Cancelled.");
        }

        return new ProcessResult(process.ExitCode, outputBuilder.ToString(), await errorTask);
    }

    private static (Window Window, ProgressBar Bar, TextBlock Status, TextBlock Percent, TextBlock Eta) CreateProgressDialog(string titleBarLabel, string heading, Action onCancel)
    {
        var (window, body) = CreateChromelessDialog(titleBarLabel);

        var statusText = new TextBlock { Text = heading, Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 13 };
        // Fixed-width slot for the live percentage so its digit count changing
        // (4% -> 45% -> 100%) can never shift the divider/ETA sitting after it.
        var percentText = new TextBlock { Text = string.Empty, Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 13, Width = 38, Margin = new Avalonia.Thickness(5, 0, 0, 0) };
        var etaText = new TextBlock { Text = string.Empty, Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 13, IsVisible = false };
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            CornerRadius = new Avalonia.CornerRadius(3),
            IsIndeterminate = true
        };
        var cancelButton = new Button { Content = "Cancel", Width = 100, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        cancelButton.Click += (_, _) =>
        {
            cancelButton.IsEnabled = false;
            statusText.Text = "Cancelling...";
            onCancel();
        };

        body.Children.Add(new TextBlock
        {
            Text = titleBarLabel,
            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 18
        });
        // Status and ETA share one line ("Exporting clip... 45% | Estimated:
        // 12s") - the divider tracks the ETA's own visibility so it only shows
        // once there's an estimate to divide from.
        var divider = new Border { Width = 1, Height = 14, Background = Avalonia.Media.Brush.Parse("#26FFFFFF"), Margin = new Avalonia.Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        divider.Bind(IsVisibleProperty, etaText.GetObservable(IsVisibleProperty));

        body.Children.Add(progressBar);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { statusText, percentText, divider, etaText }
        });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancelButton } });

        return (window, progressBar, statusText, percentText, etaText);
    }

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 1) return "less than a second";
        return remaining.TotalSeconds < 60
            ? $"{remaining.TotalSeconds:0}s"
            : $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s";
    }

    // Shared chrome for every small utility popup (confirm/message/rename) -
    // a plain Window here used the OS's own title bar (minimize/maximize/close,
    // usually light-themed on Windows), which looked jarring against the rest
    // of the app's own dark, chromeless windows (see CreateUpdateDialog). This
    // gives every popup that same slim custom title bar instead: just an icon,
    // a label, and a single close button - no minimize/maximize at all.
    private static (Window Window, Panel Body) CreateChromelessDialog(string titleBarLabel)
    {
        var window = new Window
        {
            Width = 420,
            SizeToContent = SizeToContent.Height,
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
        var titleIcon = new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://EVE/Assets/eve-icon-24.png"))),
            Width = 16,
            Height = 16,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = titleBarLabel,
            Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"),
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            // 2px down to sit on the icon's optical center - see CreateUpdateDialog.
            Margin = new Avalonia.Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleLeft = new StackPanel { Orientation = Orientation.Horizontal, Children = { titleIcon, titleText } };
        Grid.SetColumn(titleLeft, 0);
        var closeButton = new Button
        {
            Content = "✕",
            Width = 40,
            Height = 40,
            Padding = new Avalonia.Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(0),
            Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        titleBar.Children.Add(titleLeft);
        titleBar.Children.Add(closeButton);

        var body = new StackPanel { Margin = new Avalonia.Thickness(22, 20, 22, 22), Spacing = 16 };

        window.Content = new DockPanel { Children = { titleBar, body } };
        DockPanel.SetDock(titleBar, Dock.Top);

        window.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Escape) window.Close();
        };

        return (window, body);
    }

    private static Window CreateDialog(string title, string message, bool showCancel, string confirmLabel = "Delete", bool destructive = true)
    {
        var (window, body) = CreateChromelessDialog(title);

        var ok = new Button
        {
            Content = showCancel ? confirmLabel : "OK",
            Width = 100,
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (showCancel && destructive)
        {
            ok.Background = Avalonia.Media.Brush.Parse("#D95B62");
            ok.Foreground = Avalonia.Media.Brush.Parse("#FFFFFF");
        }
        else
        {
            ok.Classes.Add("primaryButton");
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
            var cancel = new Button { Content = "Cancel", Width = 100, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            cancel.Click += (_, _) => window.Close(false);
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(ok);

        body.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 18
        });
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
            FontSize = 13,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        body.Children.Add(buttons);

        return window;
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
