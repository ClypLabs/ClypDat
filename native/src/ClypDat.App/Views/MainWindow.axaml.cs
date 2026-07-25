using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using ClypDat.Capture.Abstractions;
using ClypDat.App.Converters;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using LibVLCSharp.Shared;

namespace ClypDat.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _gameDetectionTimer;
    private readonly ForegroundGameDetector _gameDetector = new();
    private Cs2GsiListener? _cs2GsiListener;
    private DotaGsiListener? _dotaGsiListener;
    private LeagueAutoClipListener? _leagueAutoClipListener;
    private PlaybackSession? _playback;
    private CancellationTokenSource? _playbackStartCts;
    private CancellationTokenSource? _editorSeekCts;
    private TimelineDragMode _timelineDragMode = TimelineDragMode.None;
    private bool _endedAtTrimBoundary;
    // Armed whenever a play session starts at/before TrimEnd, so playback
    // naturally running into it still auto-stops there (trim preview);
    // disarmed when the session instead started already past TrimEnd (user
    // explicitly seeked past it), so it can keep playing freely from there
    // instead of getting immediately stopped on the very next tick. Set once
    // per play session in StartPlayheadClock, the single place a play session
    // actually begins at a given base time.
    private bool _trimEndGuardArmed = true;
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
    // Set only while capture was started from a control living in a popup
    // (the Replay Buffer flyout), so its handlers can be detached again.
    private TopLevel? _hotkeyCaptureTopLevel;
    private DispatcherTimer? _hotkeyCaptureTimeout;
    private bool _replayTransitioning;
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
    private Window? _editorHoverControlsWindow;
    private DispatcherTimer? _hoverControlsHideTimer;
    // The bar used to vanish the instant the cursor left the video, so
    // clicking anything below it - the timeline, a transport button, the
    // volume slider - made the controls disappear mid-interaction, which read
    // as the bar being broken. It now lingers briefly after the pointer
    // leaves, so moving from the video to a control doesn't dismiss it.
    private static readonly TimeSpan HoverControlsGrace = TimeSpan.FromMilliseconds(1400);
    private DateTime _hoverControlsActiveUntilUtc = DateTime.MinValue;
    // Slides the bar in from under the video's bottom edge. The window itself
    // stays put - only its content moves - because animating an owned window's
    // native position is visibly steppy next to a composited transform.
    private const double HoverControlsSlideDistance = 72;
    private static readonly TimeSpan HoverControlsSlideDuration = TimeSpan.FromMilliseconds(190);
    private Border? _hoverControlsBackdrop;
    private DispatcherTimer? _hoverControlsSlideOutTimer;
    private bool _hoverControlsSlidingOut;
    public MainWindow()
    {
        InitializeComponent();
        // ApplySavedWindowBounds can restore straight into Maximized, which
        // won't raise an OffScreenMargin change of its own.
        RootLayout.Margin = OffScreenMargin;
        UpdateViewNavButtons();
        LibraryScrollViewer.ScrollChanged += (_, _) => UpdateDateScrubberThumb();
        // Card layout follows the grid's real width, not the window's - the
        // sidebar rail and date scrubber both sit outside this ScrollViewer.
        LibraryScrollViewer.SizeChanged += (_, sizeArgs) => ViewModel?.UpdateCardLayout(sizeArgs.NewSize.Width);
        // Card visibility flips (filtering) and hydration both change the
        // scroll extent without a size change on this window, so the marker
        // positions have to be recomputed off layout rather than only off
        // Window_OnSizeChanged.
        LibraryScrollViewer.LayoutUpdated += (_, _) => QueueDateScrubberRebuild();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playbackTimer.Tick += (_, _) => SyncPlaybackPosition();
        _gameDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameDetectionTimer.Tick += (_, _) => UpdateDetectedGame();
        Opened += (_, _) =>
        {
            // Card layout comes from LibraryScrollViewer's own SizeChanged
            // (wired above) - at Opened its width may still be 0.
            InitializeReplayServices();
            UpdateDetectedGame();
            _gameDetectionTimer.Start();
            _ = EnsureLibraryFolderAsync();
            _ = RunStartupDialogsAsync();
            _ = RefreshRemoteGameExclusionsAsync();
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
                    if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsVisible)
                        or nameof(MainWindowViewModel.IsEditorVisible)
                        or nameof(MainWindowViewModel.SelectedVideoPath)) OnViewHistoryStateChanged();
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
        SetupEditorHoverControls();
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
            _gameDetectionTimer.Stop();
            if (_replayBuffer is not null) _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
            _replayBuffer?.Dispose();
            _playback?.Dispose();
            _recordingPausedOverlay?.Close();
            _editorHoverControlsWindow?.Close();
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
        // Covers the Settings page's own hotkey button, which lives in this
        // window rather than a popup.
        AddHandler(PointerPressedEvent, HotkeyCapture_OnAnyPointerPressed, RoutingStrategies.Tunnel);
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

        HarvestGameIcons();

        if (detection.IsDetected && _replayBuffer is { IsRecording: false } && !_replayTransitioning)
        {
            _ = StartReplayBufferAsync(showErrors: false);
        }
        else if (_replayBuffer is { IsRecording: true } && !detection.IsDetected && !_replayTransitioning)
        {
            _ = StopReplayBufferAsync();
        }

        UpdateCapturePauseState(detection);
    }

    private DateTime _lastIconSweepUtc = DateTime.MinValue;

    // A game's executable path is only knowable while it's running, so icons
    // are harvested opportunistically from every game that currently has a
    // window - not just the one detection settled on, which would only ever
    // yield a single icon per session. Rate-limited because the sweep enumerates
    // every top-level window; GameIconService itself then skips anything already
    // cached or already tried.
    private void HarvestGameIcons()
    {
        if ((DateTime.UtcNow - _lastIconSweepUtc).TotalSeconds < 30) return;
        _lastIconSweepUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            IReadOnlyList<GameDetection> running;
            try
            {
                running = _gameDetector.DetectAllRunningGames();
            }
            catch (Exception error)
            {
                AppLog.Error("Game icon sweep failed", error);
                return;
            }

            foreach (var game in running)
            {
                if (!GameIconService.EnsureCached(game.DisplayName, game.ProcessId)) continue;
                var cachedGame = game.DisplayName;
                Dispatcher.UIThread.Post(() => ViewModel?.ApplyGameIcon(cachedGame));
            }
        });
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

    // Shared by the sidebar's "All Games"/per-game buttons - the "All"
    // button has no FilterOptionViewModel DataContext (it inherits the
    // window's own), so the cast falls through to null and clears the
    // filter the same way a per-game click sets it. The rail now stays
    // visible over Settings too, so a click there needs to jump back to
    // the Library to actually see the filtered result.
    private void LibraryGameSectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (ViewModel.IsSettingsVisible) ViewModel.CloseSettings();
        var key = (sender as Button)?.DataContext as FilterOptionViewModel;
        ViewModel.SelectGameSection(key?.Key);
    }

    private void LibraryClipTypeSectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (ViewModel.IsSettingsVisible) ViewModel.CloseSettings();
        var key = (sender as Button)?.DataContext as FilterOptionViewModel;
        ViewModel.SelectClipTypeSection(key?.Key);
    }

    private void ToggleGameListButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleGameListExpanded();
    }

    private void FeedbackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/ClypDat/ClypDat/issues") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error("Open feedback link failed", error);
        }
    }

    private void LibraryStorageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder)) return;
        ExplorerService.Open(ViewModel.Settings.LibraryFolder, selectFile: false);
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

    // ---- Library date scrubber ----------------------------------------
    // Replaces the library's scrollbar with a date-marker track: labels for
    // each distinct clip date, positioned at that date's real offset in the
    // scroll content, plus a viewport thumb. Positions can't be derived from
    // the data alone (a WrapPanel flows cards continuously, so a date's first
    // card can start mid-row), so they're measured from the realized
    // containers after layout. The ItemsControl here is non-virtualizing, so
    // every card has a container to measure.
    private bool _scrubberRebuildQueued;
    private bool _draggingScrubber;
    // Where inside the thumb the drag started, so the thumb keeps its grab
    // point under the cursor instead of snapping its top (or center) there.
    private double _scrubberGrabOffset;
    // RebuildDateScrubber mutates the Canvas' children, which itself triggers
    // another LayoutUpdated - without a "nothing actually changed" guard that
    // would spin forever. Keyed on everything the marker layout depends on.
    private (double Extent, double Viewport, double Track, int VisibleClips) _scrubberSignature = (-1, -1, -1, -1);
    // Each distinct date and the content offset it starts at. Pure data - the
    // only thing that renders is the bubble on the thumb, which looks up
    // whichever date the viewport currently sits in.
    private readonly List<(string Text, double ContentY)> _scrubberDates = new();

    private void QueueDateScrubberRebuild()
    {
        if (_scrubberRebuildQueued) return;
        _scrubberRebuildQueued = true;
        // Loaded priority so this runs after the layout pass that prompted
        // it - measuring before that gives every card an offset of 0.
        Dispatcher.UIThread.Post(() =>
        {
            _scrubberRebuildQueued = false;
            RebuildDateScrubber();
        }, DispatcherPriority.Loaded);
    }

    private void RebuildDateScrubber()
    {
        if (DateScrubberCanvas is null || ViewModel is null || !ViewModel.IsLibraryVisible) return;

        var trackHeight = DateScrubberHost.Bounds.Height;
        var extentHeight = LibraryScrollViewer.Extent.Height;
        var viewportHeight = LibraryScrollViewer.Viewport.Height;

        var signature = (extentHeight, viewportHeight, trackHeight, ViewModel.AllClips.Count(clip => clip.IsVisibleInLibrary));
        if (signature == _scrubberSignature) return;
        _scrubberSignature = signature;

        _scrubberDates.Clear();

        UpdateDateScrubberThumb();

        // Nothing to scrub through - everything already fits on screen.
        if (trackHeight <= 0 || extentHeight <= 0 || viewportHeight >= extentHeight) return;

        var itemsControl = LibraryScrollViewer.Content as ItemsControl ?? LibraryScrollViewer.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
        if (itemsControl is null) return;

        foreach (var container in itemsControl.GetRealizedContainers() ?? Enumerable.Empty<Control>())
        {
            if (container.DataContext is not ClipCardViewModel clip) continue;
            if (!clip.IsFirstOfDate || !clip.IsVisibleInLibrary) continue;

            var offset = container.TranslatePoint(default, itemsControl);
            if (offset is null) continue;

            // Every date is kept (nothing is drawn per-date any more, so
            // there's no crowding to thin out) - the bubble should be able to
            // name whichever one the viewport actually lands on. The year is
            // only worth showing once it stops being implied: this year's
            // clips read as "JUL 25", older ones carry the year to
            // disambiguate.
            var localDate = clip.CreatedAt.ToLocalTime();
            var format = localDate.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy";
            _scrubberDates.Add((localDate.ToString(format).ToUpperInvariant(), offset.Value.Y));
        }

        HighlightCurrentScrubberDate();
    }

    // Single shared mapping between scroll-content space and track space, so
    // the thumb, the date labels, and click/drag seeking all agree. The whole
    // content maps onto the whole track, which makes the thumb a true
    // viewport window: its top edge is the content at the top of the screen,
    // so a label lining up with the thumb's top means that date is on screen.
    private double ContentOffsetToTrackY(double contentY)
    {
        var extentHeight = LibraryScrollViewer.Extent.Height;
        if (extentHeight <= 0) return 0;
        return contentY / extentHeight * DateScrubberHost.Bounds.Height;
    }

    private void UpdateDateScrubberThumb()
    {
        if (DateScrubberThumb is null) return;

        var trackHeight = DateScrubberHost.Bounds.Height;
        var extentHeight = LibraryScrollViewer.Extent.Height;
        var viewportHeight = LibraryScrollViewer.Viewport.Height;

        if (trackHeight <= 0 || extentHeight <= 0 || viewportHeight >= extentHeight)
        {
            DateScrubberThumb.IsVisible = false;
            if (DateScrubberTrack is not null) DateScrubberTrack.IsVisible = false;
            return;
        }

        DateScrubberThumb.IsVisible = true;
        if (DateScrubberTrack is not null) DateScrubberTrack.IsVisible = true;
        DateScrubberThumb.Height = Math.Max(28, viewportHeight / extentHeight * trackHeight);
        var top = ContentOffsetToTrackY(LibraryScrollViewer.Offset.Y);
        Canvas.SetTop(DateScrubberThumb, Math.Clamp(top, 0, Math.Max(0, trackHeight - DateScrubberThumb.Height)));

        HighlightCurrentScrubberDate();
    }

    // Feeds the bubble on the thumb whichever date the top of the viewport is
    // currently inside.
    private void HighlightCurrentScrubberDate()
    {
        if (_scrubberDates.Count == 0)
        {
            if (DateScrubberBubble is not null) DateScrubberBubble.Opacity = 0;
            return;
        }

        var offsetY = LibraryScrollViewer.Offset.Y;
        var currentIndex = -1;
        for (var i = 0; i < _scrubberDates.Count; i++)
        {
            // Added in content order, so the last one at or above the
            // viewport top is the date currently on screen.
            if (_scrubberDates[i].ContentY <= offsetY + 1) currentIndex = i;
        }
        if (currentIndex < 0) currentIndex = 0;

        if (DateScrubberBubbleText is not null) DateScrubberBubbleText.Text = _scrubberDates[currentIndex].Text;
        PositionDateScrubberBubble();
    }

    // Vertically centred on the thumb and hung off the left of the rail, so
    // it reads as attached to the handle you're actually dragging.
    private void PositionDateScrubberBubble()
    {
        if (DateScrubberBubble is null || DateScrubberThumb is null) return;

        var thumbTop = Canvas.GetTop(DateScrubberThumb);
        if (double.IsNaN(thumbTop)) thumbTop = 0;
        var bubbleHeight = DateScrubberBubble.Bounds.Height;
        var bubbleWidth = DateScrubberBubble.Bounds.Width;
        if (bubbleHeight <= 0) bubbleHeight = 24;

        Canvas.SetTop(DateScrubberBubble, thumbTop + DateScrubberThumb.Bounds.Height / 2 - bubbleHeight / 2);
        Canvas.SetLeft(DateScrubberBubble, -(bubbleWidth > 0 ? bubbleWidth : 64) - 10);
    }

    // y is where the thumb's TOP should land, which by the shared mapping
    // above is exactly the content offset to scroll to - no half-viewport
    // fudge, which is what made clicking a date land short of it and made
    // the thumb slide out from under the cursor mid-drag.
    private void SeekLibraryToThumbTop(double y)
    {
        var trackHeight = DateScrubberHost.Bounds.Height;
        var extentHeight = LibraryScrollViewer.Extent.Height;
        var viewportHeight = LibraryScrollViewer.Viewport.Height;
        if (trackHeight <= 0 || extentHeight <= 0) return;

        var target = y / trackHeight * extentHeight;
        LibraryScrollViewer.Offset = new Vector(
            LibraryScrollViewer.Offset.X,
            Math.Clamp(target, 0, Math.Max(0, extentHeight - viewportHeight)));
        UpdateDateScrubberThumb();
    }

    private void DateScrubber_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(DateScrubberHost).Properties.IsLeftButtonPressed) return;
        if (!DateScrubberThumb.IsVisible) return;

        var y = e.GetPosition(DateScrubberHost).Y;
        var thumbTop = Canvas.GetTop(DateScrubberThumb);
        var thumbHeight = DateScrubberThumb.Bounds.Height;

        if (y >= thumbTop && y <= thumbTop + thumbHeight)
        {
            // Grabbed the thumb itself - keep the grab point pinned to the
            // cursor for the rest of the drag.
            _scrubberGrabOffset = y - thumbTop;
        }
        else
        {
            // Clicked the bare track - center the thumb there and drag from
            // its middle.
            _scrubberGrabOffset = thumbHeight / 2;
            SeekLibraryToThumbTop(y - _scrubberGrabOffset);
        }

        _draggingScrubber = true;
        e.Pointer.Capture(DateScrubberHost);
        SetScrubberThumbState(hovered: true, dragging: true);
        // The date timeline is a drag-time affordance only - it floats over
        // the library rather than reserving space, so it stays hidden until
        // there's actually a scrub in progress to label.
        DateScrubberBubble.Opacity = _scrubberDates.Count > 0 ? 1 : 0;
        PositionDateScrubberBubble();
        e.Handled = true;
    }

    // Idle stays deliberately quiet so the library isn't competing with a
    // bright scrollbar; hover widens it, dragging turns it accent.
    private void SetScrubberThumbState(bool hovered, bool dragging)
    {
        if (DateScrubberThumb is null) return;

        DateScrubberThumb.Background = dragging
            ? (Avalonia.Media.IBrush?)Application.Current?.FindResource("AccentBrush") ?? Avalonia.Media.Brush.Parse("#5864E8")
            : Avalonia.Media.Brush.Parse(hovered ? "#55697E" : "#3A4857");
        DateScrubberThumb.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(hovered ? "scaleX(1.75)" : "scaleX(1)");
        if (DateScrubberTrack is not null)
        {
            DateScrubberTrack.Background = Avalonia.Media.Brush.Parse(hovered ? "#1B2530" : "#141C24");
        }
    }

    private void DateScrubber_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingScrubber) return;
        SeekLibraryToThumbTop(e.GetPosition(DateScrubberHost).Y - _scrubberGrabOffset);
    }

    private void DateScrubber_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingScrubber) return;
        _draggingScrubber = false;
        e.Pointer.Capture(null);
        // Still under the cursor right after releasing, so settle into hover
        // rather than all the way back to idle.
        SetScrubberThumbState(hovered: DateScrubberHost.IsPointerOver, dragging: false);
        DateScrubberBubble.Opacity = 0;
    }

    private void DateScrubber_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_draggingScrubber) return;
        SetScrubberThumbState(hovered: true, dragging: false);
    }

    private void DateScrubber_OnPointerExited(object? sender, PointerEventArgs e)
    {
        // Pointer capture keeps a drag alive past the track's edges, so this
        // only handles the plain hover-out case.
        if (_draggingScrubber) return;
        SetScrubberThumbState(hovered: false, dragging: false);
    }

    private async void OpenReplaySettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectSettingsSection("Replay Buffer");
        ViewModel.OpenSettings();
        await ViewModel.RefreshOpenProcessesAsync();
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
            ViewModel.RecorderStatus = "Replay Armed";
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
                ViewModel.RecorderStatus = "Replay Armed";
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
            ViewModel.RecorderStatus = "Replay Armed";
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
                // recording (e.g. CS2 launched but ClypDat hasn't caught up yet) isn't
                // worth interrupting the user over - just drop it.
                if (isAutoClip) return;
                if (ViewModel.IsReplayRecording) ViewModel.IsReplayRecording = false;
                await ShowMessageAsync("Clip failed", "Replay is armed, but no game is being captured yet.");
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
                else
                {
                    // Manual hotkey press had NO feedback at all until the remux
                    // (RemuxWindowToMp4 - thousands of native FFmpeg calls, can
                    // take a real fraction of a second to over a second for a
                    // long buffer) finished below - the press felt unregistered
                    // the whole time it was working. Auto-clips already got this
                    // via the branch above; manual presses just never had the
                    // equivalent.
                    ShowClipNotification("Saving clip…", playSound: false);
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

    // Only one clip-notification overlay at a time - a fast save ("Saving
    // clip..." immediately followed by "Clip saved" before the first one's
    // 2.2s dwell even finished) used to leave two separate overlay Windows
    // stacked on top of each other at the same position, visibly overlapping/
    // flickering. A new notification now instantly closes whatever's already
    // showing before presenting itself, instead of piling on top of it.
    private Window? _activeClipOverlay;
    private DispatcherTimer? _activeClipOverlayCloseTimer;

    private void ShowClipSavedNotification()
    {
        ShowClipNotification("Clip saved", playSound: true);
    }

    private void ShowClipNotification(string text, bool playSound)
    {
        if (ViewModel is null) return;
        if (ViewModel.Settings.EnableClipOverlay)
        {
            try
            {
                ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition, text, playSound);
            }
            catch (Exception error)
            {
                AppLog.Error("Clip notification overlay failed", error);
            }
        }
        else if (playSound && ViewModel.Settings.EnableClipOverlaySound)
        {
            // Overlay's off but the sound is still wanted - nothing to time it
            // against, so just play it immediately.
            PlayClipNotificationSound();
        }
    }

    private void PlayClipNotificationSound()
    {
        if (ViewModel is null || !ViewModel.Settings.EnableClipOverlaySound) return;
        try
        {
            ClipNotificationSound.Play(ViewModel.Settings.ClipOverlayVolume);
        }
        catch (Exception error)
        {
            AppLog.Error("Clip notification sound failed", error);
        }
    }

    private void ShowClipSavedOverlay(string position, string text, bool playSound)
    {
        _activeClipOverlayCloseTimer?.Stop();
        _activeClipOverlayCloseTimer = null;
        _activeClipOverlay?.Close();
        _activeClipOverlay = null;

        var isLeft = string.Equals(position, "Top Left", StringComparison.OrdinalIgnoreCase);

        // A full-height accent stripe (not a small dot) plus a solid, near-
        // opaque background - meant to actually stand out at a glance over
        // gameplay, not blend in as a subtle little pill.
        var accent = new Border
        {
            Width = 7,
            Background = Avalonia.Media.Brush.Parse("#13C8B5"),
            CornerRadius = isLeft ? new CornerRadius(4, 0, 0, 4) : new CornerRadius(0, 4, 4, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var label = new TextBlock
        {
            Text = text,
            Foreground = Avalonia.Media.Brush.Parse("#F5F9FF"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 19,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(22, 20, 26, 20),
            Children = { label }
        };
        var translate = new TranslateTransform();
        var badge = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#F5141D24"),
            BorderBrush = Avalonia.Media.Brush.Parse("#3C4C5A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 10 28 0 #70000000"),
            RenderTransform = translate,
            Opacity = 0,
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    accent,
                    content
                }
            }
        };
        DockPanel.SetDock(accent, isLeft ? Dock.Left : Dock.Right);

        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredWidth = badge.DesiredSize.Width;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;
        var marginDevicePixels = (int)Math.Round(24 * scaling);
        var widthDevicePixels = (int)Math.Round(desiredWidth * scaling);
        var x = isLeft
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

        // Slides in FROM the edge it's pinned to, toward its resting position -
        // left-pinned slides in moving right, right-pinned slides in moving left
        // (the "reverse"). Set before Show() (no transition yet, so this is the
        // instant starting state, not an animated jump), then flipped to
        // identity/opaque one frame later so the Transitions below actually have
        // a "from" state to animate away from instead of both values landing in
        // the same layout pass with nothing visibly in between.
        const double SlideDistance = 28;
        translate.X = isLeft ? -SlideDistance : SlideDistance;

        _activeClipOverlay = overlay;
        overlay.Show();

        badge.Transitions =
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(200)
            }
        ];
        translate.Transitions =
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(260),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut()
            }
        ];
        Dispatcher.UIThread.Post(() =>
        {
            badge.Opacity = 1;
            translate.X = 0;
            // Sound used to fire the instant this method was called - well
            // before the slide/fade-in transition below even started, so it
            // landed a couple hundred ms ahead of anything visibly happening.
            // Playing it here instead, right as the "pop in" begins, actually
            // lines the two up.
            if (playSound) PlayClipNotificationSound();
        }, DispatcherPriority.Loaded);

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            _activeClipOverlayCloseTimer = null;
            // Slide back out the same way it came in, then close once that
            // transition has actually had time to finish playing.
            badge.Opacity = 0;
            translate.X = isLeft ? -SlideDistance : SlideDistance;
            var closeAfterExit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            closeAfterExit.Tick += (_, _) =>
            {
                closeAfterExit.Stop();
                overlay.Close();
                if (_activeClipOverlay == overlay) _activeClipOverlay = null;
            };
            closeAfterExit.Start();
        };
        _activeClipOverlayCloseTimer = closeTimer;
        closeTimer.Start();
    }

    private void ReplayBuffer_OnRecordingStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not null)
            {
                ViewModel.IsReplayRecording = false;
                ViewModel.RecorderStatus = "Replay Armed";
            }
        });
    }

    // Best-effort, throttled internally (see RemoteGameExclusionsService) so
    // this is safe to fire on every startup - only actually hits the network
    // roughly once a day. _gameDetector already has whatever was cached from
    // a previous successful fetch applied synchronously before this ever
    // runs (see its constructor), so a slow/failed network here just means
    // this session doesn't get today's update, not that detection has no
    // remote list at all.
    private async Task RefreshRemoteGameExclusionsAsync()
    {
        var updated = await RemoteGameExclusionsService.RefreshAsync();
        if (updated is not null) _gameDetector.ApplyRemoteIgnoredExecutables(updated);

        // Curated icon overrides ride the same once-a-day cadence. Only used
        // for games Wikidata/Steam resolve wrongly or not at all, so a failure
        // here costs nothing.
        await RemoteGameIconsService.RefreshAsync();
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

    // First run: ClypDat gets a Videos\ClypDat folder with the standard Clips/VODs
    // layout, so recording never blocks behind a folder picker.
    private static string DefaultLibraryFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ClypDat");

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

        // Width above is a one-time snapshot of CardWidth at the moment
        // editing started, same as the static TextBlock it's covering (see
        // that field's own comment for why a fixed Width, not just MaxWidth,
        // is needed) - but the card itself keeps resizing reactively via a
        // live CardWidth binding while the window is dragged, so without
        // this the edit box visibly stopped tracking the card's own width
        // the instant editing began, going stale/mismatched on any resize.
        void SyncWidthToCard(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(MainWindowViewModel.CardWidth) || ViewModel is null) return;
            editBox.Width = Math.Max(80, ViewModel.CardWidth - 32);
        }

        if (ViewModel is not null) ViewModel.PropertyChanged += SyncWidthToCard;

        // Enter/blur/click-elsewhere all commit, Escape cancels - guarded by
        // resolved so removing the box (which can itself trigger a blur)
        // can't re-fire and commit a second time.
        var resolved = false;

        async void Resolve(bool save)
        {
            if (resolved) return;
            resolved = true;

            if (ViewModel is not null) ViewModel.PropertyChanged -= SyncWidthToCard;
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

    // Custom title bar (native caption buttons removed via
    // ExtendClientAreaChromeHints="NoChrome") - clicking anywhere else in
    // the header bar drags the window, matching native title bar behavior;
    // a second click within Avalonia's double-click window toggles
    // maximize instead, same as double-clicking a native title bar.
    private void HeaderBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }
        BeginMoveDrag(e);
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // Keeps the custom maximize/restore glyph in sync however WindowState
    // changes - the button itself, the double-click-header shortcut above,
    // OS-level snap/restore, and ApplySavedWindowBounds restoring a
    // maximized state on startup all go through this same property.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Windows oversizes a maximized window by the resize-border width so
        // the border sits offscreen. With the client area extended into the
        // decorations there's no chrome absorbing that, so the layout has to
        // inset itself by the same amount or its edges are clipped away.
        if (change.Property == OffScreenMarginProperty && RootLayout is not null)
        {
            RootLayout.Margin = OffScreenMargin;
        }

        if (change.Property == WindowStateProperty && MaximizeRestoreButton?.Content is PathIcon icon)
        {
            var isMaximized = WindowState == WindowState.Maximized;
            icon.Data = Geometry.Parse(isMaximized
                ? "M19,5H8C6.9,5,6,5.9,6,7v14c0,1.1,0.9,2,2,2h11c1.1,0,2-0.9,2-2V7C21,5.9,20.1,5,19,5z M19,21H8V7h11V21z M16,1H4C2.9,1,2,1.9,2,3v14h2V3h12V1z"
                : "M18,4H6C4.9,4,4,4.9,4,6v12c0,1.1,0.9,2,2,2h12c1.1,0,2-0.9,2-2V6C20,4.9,19.1,4,18,4z M18,18H6V6h12V18z");
            ToolTip.SetTip(MaximizeRestoreButton, isMaximized ? "Restore" : "Maximize");
        }
    }

    // Back/Forward header nav across all three top-level views. The Editor
    // needs its clip path recorded alongside the view kind, since re-entering
    // it means reopening one specific clip rather than just flipping a flag -
    // without that it couldn't be in the history at all, which is why Back
    // sat permanently disabled while a clip was open.
    private enum ViewHistoryKind { Library, Settings, Editor }

    private readonly record struct ViewHistoryEntry(ViewHistoryKind Kind, string? ClipPath);

    private readonly List<ViewHistoryEntry> _viewHistory = new() { new ViewHistoryEntry(ViewHistoryKind.Library, null) };
    private int _viewHistoryIndex;
    private bool _navigatingViewHistory;

    private ViewHistoryEntry CurrentViewState()
    {
        if (ViewModel is null) return new ViewHistoryEntry(ViewHistoryKind.Library, null);
        if (ViewModel.IsEditorVisible) return new ViewHistoryEntry(ViewHistoryKind.Editor, ViewModel.SelectedVideoPath);
        if (ViewModel.IsSettingsVisible) return new ViewHistoryEntry(ViewHistoryKind.Settings, null);
        return new ViewHistoryEntry(ViewHistoryKind.Library, null);
    }

    private void OnViewHistoryStateChanged()
    {
        if (_navigatingViewHistory || ViewModel is null) return;

        var state = CurrentViewState();
        // Opening a clip raises IsEditorVisible and SelectedVideoPath
        // separately, so this runs twice per open - the equality check keeps
        // that from pushing a duplicate entry.
        if (_viewHistory[_viewHistoryIndex] == state) return;

        // An editor entry that arrives before its path is set would record a
        // clip-less Editor state that Forward can't reopen.
        if (state.Kind == ViewHistoryKind.Editor && string.IsNullOrWhiteSpace(state.ClipPath)) return;

        _viewHistory.RemoveRange(_viewHistoryIndex + 1, _viewHistory.Count - _viewHistoryIndex - 1);
        _viewHistory.Add(state);
        _viewHistoryIndex++;
        UpdateViewNavButtons();
    }

    private void UpdateViewNavButtons()
    {
        BackNavButton.IsEnabled = _viewHistoryIndex > 0;
        ForwardNavButton.IsEnabled = _viewHistoryIndex < _viewHistory.Count - 1;
    }

    // Shared by history navigation and the editor's own close button so both
    // tear playback down the same way.
    private void CloseEditorForNavigation()
    {
        if (ViewModel is null || !ViewModel.IsEditorVisible) return;
        ViewModel.SaveSelectedClipEditState();
        StopEditorPlayback(stopPlaybackAsync: true);
        ViewModel.CloseEditor();
    }

    private async Task ApplyViewHistoryEntryAsync()
    {
        if (ViewModel is null) return;
        _navigatingViewHistory = true;
        try
        {
            var entry = _viewHistory[_viewHistoryIndex];
            switch (entry.Kind)
            {
                case ViewHistoryKind.Editor:
                    var clip = ViewModel.AllClips.FirstOrDefault(c => string.Equals(c.Path, entry.ClipPath, StringComparison.OrdinalIgnoreCase));
                    // Deleted or renamed since it was visited - drop the entry
                    // rather than stranding the user on a dead history slot.
                    if (clip is null)
                    {
                        _viewHistory.RemoveAt(_viewHistoryIndex);
                        _viewHistoryIndex = Math.Max(0, _viewHistoryIndex - 1);
                        break;
                    }
                    if (ViewModel.IsSettingsVisible) ViewModel.CloseSettings();
                    if (!string.Equals(ViewModel.SelectedVideoPath, entry.ClipPath, StringComparison.OrdinalIgnoreCase) || !ViewModel.IsEditorVisible)
                    {
                        await OpenClipCardAsync(clip);
                    }
                    break;

                case ViewHistoryKind.Settings:
                    // Close the editor first so CloseSettings' own
                    // "restore whatever was open before" doesn't bring it back.
                    CloseEditorForNavigation();
                    if (!ViewModel.IsSettingsVisible) ViewModel.OpenSettings();
                    break;

                default:
                    if (ViewModel.IsSettingsVisible) ViewModel.CloseSettings();
                    CloseEditorForNavigation();
                    break;
            }
        }
        finally
        {
            _navigatingViewHistory = false;
        }
        UpdateViewNavButtons();
    }

    private async void BackNavButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewHistoryIndex <= 0) return;
        _viewHistoryIndex--;
        await ApplyViewHistoryEntryAsync();
    }

    private async void ForwardNavButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewHistoryIndex >= _viewHistory.Count - 1) return;
        _viewHistoryIndex++;
        await ApplyViewHistoryEntryAsync();
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
        HideEditorHoverControls(immediate: true);

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

    // The ClypDat logo button is a universal "go back to Library" from anywhere
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
            // The detector holds its own copy of the ignore list, so without
            // pushing the update it keeps detecting the game just removed -
            // and re-adds it on the next tick.
            _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
            UpdateDetectedGame();
        }
    }

    private void UpdateAutoClipStates()
    {
        if (ViewModel is null) return;
        UpdateCs2AutoClipState();
        UpdateDotaAutoClipState();
        UpdateLeagueAutoClipState();
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
            var (whatsNew, fixes) = await AppUpdateService.GetCurrentVersionNotesAsync();
            var upToDateDialog = CreateUpToDateDialog(whatsNew, fixes);
            await upToDateDialog.ShowDialog(this);
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
            Process.Start(new ProcessStartInfo("https://github.com/ClypDat/ClypDat") { UseShellExecute = true });
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

    private void EditorPathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        ExplorerService.Open(ViewModel.SelectedVideoPath, selectFile: true);
    }

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
    {
        EndHotkeyCapture();
        if (ViewModel is null) return;
        ViewModel.IsCapturingHotkey = true;

        // The key handlers live on this window, but a Flyout hosts its content
        // in its own popup top level - keystrokes there never route through
        // here, so capture started from the Replay Buffer flyout silently
        // received nothing. Worse, IsCapturingHotkey stayed true after the
        // flyout was dismissed, and the capture branch swallows every key it
        // sees, which is what left the save hotkey dead afterwards. Attach the
        // same handlers to whichever top level the button actually belongs to.
        var buttonTopLevel = sender is Control control ? TopLevel.GetTopLevel(control) : null;
        if (buttonTopLevel is not null && !ReferenceEquals(buttonTopLevel, this))
        {
            _hotkeyCaptureTopLevel = buttonTopLevel;
            buttonTopLevel.AddHandler(KeyDownEvent, MainWindow_OnKeyDown, RoutingStrategies.Tunnel);
            buttonTopLevel.AddHandler(KeyUpEvent, MainWindow_OnKeyUp, RoutingStrategies.Tunnel);
            // Clicks inside the flyout don't reach this window either, so the
            // cancel-on-click-away handler has to live on the popup too.
            buttonTopLevel.AddHandler(PointerPressedEvent, HotkeyCapture_OnAnyPointerPressed, RoutingStrategies.Tunnel);
        }
        else
        {
            Focus();
        }

        // Backstop: dismissing the flyout (or just walking away) must never
        // leave capture armed, because while it is armed every keystroke in
        // the app is swallowed.
        _hotkeyCaptureTimeout ??= new DispatcherTimer();
        _hotkeyCaptureTimeout.Interval = TimeSpan.FromSeconds(6);
        _hotkeyCaptureTimeout.Stop();
        _hotkeyCaptureTimeout.Tick -= HotkeyCaptureTimeout_OnTick;
        _hotkeyCaptureTimeout.Tick += HotkeyCaptureTimeout_OnTick;
        _hotkeyCaptureTimeout.Start();
    }

    private void HotkeyCaptureTimeout_OnTick(object? sender, EventArgs e)
    {
        AppLog.Debug("Hotkey capture timed out without a key press - cancelling.");
        EndHotkeyCapture();
    }

    // Clicking anywhere else abandons capture, leaving the existing hotkey
    // untouched. Safe against the click that starts capture: Click fires after
    // PointerPressed, so capture isn't armed yet when that press is seen.
    private void HotkeyCapture_OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsCapturingHotkey != true) return;
        AppLog.Debug("Hotkey capture cancelled - clicked away.");
        EndHotkeyCapture();
    }

    // Detaches the popup handlers and disarms capture. Safe to call when
    // capture was never started.
    private void EndHotkeyCapture()
    {
        _hotkeyCaptureTimeout?.Stop();
        if (_hotkeyCaptureTopLevel is not null)
        {
            _hotkeyCaptureTopLevel.RemoveHandler(KeyDownEvent, MainWindow_OnKeyDown);
            _hotkeyCaptureTopLevel.RemoveHandler(KeyUpEvent, MainWindow_OnKeyUp);
            _hotkeyCaptureTopLevel.RemoveHandler(PointerPressedEvent, HotkeyCapture_OnAnyPointerPressed);
            _hotkeyCaptureTopLevel = null;
        }

        _capturedHotkeyKeys.Clear();
        if (ViewModel is not null) ViewModel.IsCapturingHotkey = false;
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
                EndHotkeyCapture();
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
                AppLog.Info($"Save hotkey set to {hotkey}.");
            }

            // Detaches the popup handlers too - SetHotkey only clears the flag.
            EndHotkeyCapture();
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
            //
            // Position is read AFTER Pause() (not before) - VideoPlayer.SetPause(true)
            // doesn't land instantly, so a snapshot taken beforehand is a moment
            // behind wherever the video actually freezes. Resuming from that stale,
            // earlier snapshot was rewinding to a spot already played and replaying
            // it forward - visible as a brief "rewind and repeat" on every unpause.
            _playback.Pause();
            var pauseTime = _playback.Position;
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

        var tempPath = Path.Combine(Path.GetTempPath(), $"clypdat-save-trim-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");

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
            var backupPath = sourcePath + ".clypdat-trim-backup";
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
            Width = 680,
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
        var titleIcon = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-24.png"))), Width = 16, Height = 16, Margin = new Avalonia.Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // 2px down: TextBlock's cap height sits visually higher than the icon's
        // optical center at this size, reading as misaligned despite both being
        // VerticalAlignment=Center.
        var titleText = new TextBlock { Text = "Update available", Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleLeft = new StackPanel { Orientation = Orientation.Horizontal, Children = { titleIcon, titleText } };
        Grid.SetColumn(titleLeft, 0);
        CancellationTokenSource? downloadCts = null;
        var closeButton = new Button { Content = "✕", Width = 40, Height = 40, Padding = new Avalonia.Thickness(0), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(0), Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        // downloadCts is null until Update Now starts one - X used to just
        // close the window while DownloadAndRestartAsync kept running
        // undisturbed in the background (nothing was ever cancelling it), so
        // closing out of the dialog mid-download still silently installed
        // the update anyway. Cancelling here unwinds the download loop
        // (DownloadAndRestartAsync already threads the token through every
        // read/write) before it ever reaches the actual file-swap script.
        closeButton.Click += (_, _) =>
        {
            downloadCts?.Cancel();
            window.Close();
        };
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
        var etaText = new TextBlock
        {
            Text = string.Empty,
            Foreground = Avalonia.Media.Brush.Parse("#5C6D7E"),
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
            downloadCts = new CancellationTokenSource();
            var downloadClock = Stopwatch.StartNew();
            // Same elapsed/fraction extrapolation ExportCurrentClipAsync's own
            // progress handler uses - below a few percent one early sample
            // would wildly overshoot, so hold off showing anything yet.
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                statusText.Text = value.Status;
                progressBar.IsIndeterminate = value.Percentage is null;
                if (value.Percentage is not null)
                {
                    progressBar.Value = value.Percentage.Value * 100;
                    if (value.Percentage.Value > 0.03)
                    {
                        var remaining = TimeSpan.FromMilliseconds(downloadClock.ElapsedMilliseconds * (1 - value.Percentage.Value) / value.Percentage.Value);
                        etaText.Text = $"Estimated: {FormatEta(remaining)}";
                        etaText.IsVisible = true;
                    }
                }
                else
                {
                    etaText.IsVisible = false;
                }
            });

            try
            {
                await AppUpdateService.DownloadAndRestartAsync(update, progress, downloadCts.Token);
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
            catch (OperationCanceledException)
            {
                // User hit X mid-download (see closeButton.Click) - the window
                // is already closing, nothing left to clean up or report.
            }
            catch (Exception error)
            {
                AppLog.Error("Update install failed", error);
                await ShowMessageAsync("Update failed", $"ClypDat could not install the update.\n\n{error.Message}");
                updateButton.IsEnabled = true;
                laterButton.IsEnabled = true;
                ignoreButton.IsEnabled = true;
                statusText.IsVisible = false;
                progressBar.IsVisible = false;
                etaText.IsVisible = false;
            }
        };

        var notesGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        var whatsNewColumn = BuildNotesColumn("What's New", update.WhatsNew, "#13C8B5");
        var fixesColumn = BuildNotesColumn("Fixes", update.Fixes, "#E0A94A");
        Grid.SetColumn(whatsNewColumn, 0);
        Grid.SetColumn(fixesColumn, 2);
        notesGrid.Children.Add(whatsNewColumn);
        notesGrid.Children.Add(fixesColumn);

        var hero = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 20, 22, 16),
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"ClypDat {FormatVersion(update.LatestVersion)}",
                            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                            FontSize = 20
                        },
                        new Border
                        {
                            Background = Avalonia.Media.Brush.Parse("#1C3A36"),
                            CornerRadius = new Avalonia.CornerRadius(5),
                            Padding = new Avalonia.Thickness(7, 2),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = "AVAILABLE",
                                Foreground = Avalonia.Media.Brush.Parse("#13C8B5"),
                                FontSize = 10,
                                FontWeight = Avalonia.Media.FontWeight.Bold
                            }
                        }
                    }
                },
                new TextBlock
                {
                    Text = $"You're on {FormatVersion(update.CurrentVersion)}.",
                    Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
                    FontSize = 13
                }
            }
        };

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 0, 22, 16),
            Children = { notesGrid }
        };

        // Status/progress/buttons live in a fixed footer, never inside the
        // scrollable notes columns - previously a long release-notes list
        // pushed Update Now/Later/Skip below the fold entirely.
        var footer = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 0, 22, 20),
            Spacing = 10,
            Children =
            {
                statusText,
                etaText,
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

        window.Content = new DockPanel
        {
            Children =
            {
                titleBar,
                footer,
                hero,
                body
            }
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(hero, Dock.Top);

        return window;
    }

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    // Shared by CreateUpdateDialog and CreateUpToDateDialog: one scrollable
    // column ("What's New" / "Fixes"), each independently scrollable (not one
    // shared scroll over everything - a long What's New list used to push
    // Fixes, and the buttons, off screen together). Accent color distinguishes
    // the two at a glance; an empty list still renders the column with a quiet
    // placeholder instead of collapsing it, so the two-box layout stays a
    // two-box layout even on a release that only touched one side.
    private const int NotesColumnAreaHeight = 320;
    private static Border BuildNotesColumn(string title, IReadOnlyList<string> notes, string accentHex)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(14, 12, 14, 8),
            Children =
            {
                new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Background = Avalonia.Media.Brush.Parse(accentHex),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = title,
                    Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = notes.Count.ToString(),
                    Foreground = Avalonia.Media.Brush.Parse("#5C6D7E"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsVisible = notes.Count > 0
                }
            }
        };

        var list = new StackPanel { Spacing = 9, Margin = new Avalonia.Thickness(14, 0, 12, 14) };
        if (notes.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "Nothing here this update.",
                Foreground = Avalonia.Media.Brush.Parse("#5C6D7E"),
                FontSize = 12,
                FontStyle = Avalonia.Media.FontStyle.Italic
            });
        }
        foreach (var note in notes)
        {
            list.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 4,
                        Height = 4,
                        CornerRadius = new Avalonia.CornerRadius(2),
                        Background = Avalonia.Media.Brush.Parse(accentHex),
                        Margin = new Avalonia.Thickness(0, 7, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    },
                    new TextBlock
                    {
                        Text = note,
                        Foreground = Avalonia.Media.Brush.Parse("#C4D2E0"),
                        FontSize = 13,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        // A horizontal StackPanel measures children with
                        // unbounded width, so TextWrapping does nothing
                        // without an explicit Width to actually wrap
                        // against. The window is fixed-size (CanResize
                        // false) at 680, so this is safe as a constant:
                        // 680 - hero/body's 44 side margin, split into two
                        // equal Grid columns (minus the 14 gap column),
                        // minus each column's own list margin (26) and
                        // this row's own bullet dot + spacing (12), minus
                        // a little slack for the column's ScrollViewer
                        // reserving space for its scrollbar track.
                        Width = 260
                    }
                }
            });
        }

        DockPanel.SetDock(header, Dock.Top);
        return new Border
        {
            Background = Avalonia.Media.Brush.Parse("#0C1319"),
            BorderBrush = Avalonia.Media.Brush.Parse("#1E2A34"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Child = new DockPanel
            {
                Children =
                {
                    header,
                    new ScrollViewer { Content = list, MaxHeight = NotesColumnAreaHeight }
                }
            }
        };
    }

    // "You're up to date" dialog - shares CreateUpdateDialog's chrome/notes-
    // column styling instead of the plain generic ShowMessageAsync box, and
    // shows what the currently installed version actually shipped (via
    // AppUpdateService.GetCurrentVersionNotesAsync) instead of just a bare
    // version number.
    private Window CreateUpToDateDialog(IReadOnlyList<string> whatsNew, IReadOnlyList<string> fixes)
    {
        var window = new Window
        {
            Width = 680,
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
        var titleIcon = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-24.png"))), Width = 16, Height = 16, Margin = new Avalonia.Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock { Text = "You're up to date", Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleLeft = new StackPanel { Orientation = Orientation.Horizontal, Children = { titleIcon, titleText } };
        Grid.SetColumn(titleLeft, 0);
        var closeButton = new Button { Content = "✕", Width = 40, Height = 40, Padding = new Avalonia.Thickness(0), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(0), Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        titleBar.Children.Add(titleLeft);
        titleBar.Children.Add(closeButton);

        var notesGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        var whatsNewColumn = BuildNotesColumn("What's New", whatsNew, "#13C8B5");
        var fixesColumn = BuildNotesColumn("Fixes", fixes, "#E0A94A");
        Grid.SetColumn(whatsNewColumn, 0);
        Grid.SetColumn(fixesColumn, 2);
        notesGrid.Children.Add(whatsNewColumn);
        notesGrid.Children.Add(fixesColumn);

        var hero = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 20, 22, 16),
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"ClypDat {FormatVersion(AppUpdateService.CurrentVersion)}",
                            Foreground = Avalonia.Media.Brush.Parse("#EDF4FB"),
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                            FontSize = 20
                        },
                        new Border
                        {
                            Background = Avalonia.Media.Brush.Parse("#1C3345"),
                            CornerRadius = new Avalonia.CornerRadius(5),
                            Padding = new Avalonia.Thickness(7, 2),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = "LATEST",
                                Foreground = Avalonia.Media.Brush.Parse("#5AA9E0"),
                                FontSize = 10,
                                FontWeight = Avalonia.Media.FontWeight.Bold
                            }
                        }
                    }
                },
                new TextBlock
                {
                    Text = "You're running the latest version. Here's what it brought:",
                    Foreground = Avalonia.Media.Brush.Parse("#8EA1B6"),
                    FontSize = 13
                }
            }
        };

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 0, 22, 16),
            Children = { notesGrid }
        };

        var footer = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 0, 22, 20),
            Children =
            {
                new Button
                {
                    Content = "Got it",
                    Width = 120,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Classes = { "primaryButton" }
                }
            }
        };
        ((Button)footer.Children[0]).Click += (_, _) => window.Close();

        window.Content = new DockPanel
        {
            Children =
            {
                titleBar,
                footer,
                hero,
                body
            }
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(hero, Dock.Top);

        return window;
    }

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
        HideEditorHoverControls(immediate: true);
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
    // and even while ClypDat itself is minimized) instead of being scoped to
    // ClypDat. An owned Window (Owner = this, no Topmost) gets normal Win32
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
            if (_editorHoverControlsWindow is { IsVisible: true } hoverBar) RepositionEditorHoverControls(hoverBar);
        };
        EditorVideoView.LayoutUpdated += (_, _) =>
        {
            if (_recordingPausedOverlay is { IsVisible: true } overlay) RepositionPausedOverlay(overlay);
            if (_editorHoverControlsWindow is { IsVisible: true } hoverBar) RepositionEditorHoverControls(hoverBar);
            // Covers window resize AND the fullscreen reparent (both change
            // EditorVideoView's rendered height, which the pan-range math
            // depends on) without needing separate handlers for each.
            UpdateVideoTransform();
        };
    }

    // The video hover bar mirrors the "Recording Paused" badge's owned-window
    // technique above (see RepositionPausedOverlay/EnsureRecordingPausedOverlay) -
    // LibVLCSharp's VideoView is a native (non-Avalonia) hwnd on Windows that
    // always paints over Avalonia-rendered siblings regardless of z-order, so a
    // plain in-tree Avalonia overlay would never actually show above the video.
    // An owned Window (Owner = this, no Topmost) sits above it correctly while
    // staying scoped to ClypDat and hidden/minimized together with its owner.
    // PointerEntered/PointerMoved/PointerExited on EditorVideoHost never fire
    // while the cursor is actually over the video - LibVLCSharp's VideoView is
    // a native child hwnd occupying that whole area, and Windows routes plain
    // mouse-move input straight to that hwnd instead of through Avalonia's
    // input pipeline (mouse WHEEL routes differently, which is why zoom-via-
    // scroll on this same host already worked). Polling the real cursor
    // position against the video's on-screen rect sidesteps that entirely.
    private void SetupEditorHoverControls()
    {
        _hoverControlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        // Guarded: an exception escaping a DispatcherTimer tick kills the
        // subscription, and this poll touches things that can legitimately
        // throw mid-transition - PointToScreen on a visual that's momentarily
        // detached (the fullscreen reparent), or RenderScaling on a window
        // being torn down. One throw and the bar would never appear again for
        // the rest of the session, which matches it "randomly" going away and
        // staying away.
        _hoverControlsHideTimer.Tick += (_, _) =>
        {
            try
            {
                PollEditorHoverControls();
            }
            catch (Exception error)
            {
                AppLog.Error("Editor hover bar poll failed (recovered)", error);
            }
        };
        _hoverControlsHideTimer.Start();
    }

    // Only logged on an actual show/hide transition - the poll runs ~8x a
    // second and would otherwise bury the log.
    private string _hoverControlsLastState = string.Empty;

    private void LogHoverControlsState(string state)
    {
        if (_hoverControlsLastState == state) return;
        _hoverControlsLastState = state;
        AppLog.Debug($"Editor hover bar: {state}.");
    }

    private void PollEditorHoverControls()
    {
        if (ViewModel is null || !ViewModel.IsEditorVisible || ViewModel.IsVideoFullscreen || _playback is null)
        {
            if (_editorHoverControlsWindow is { IsVisible: true })
            {
                LogHoverControlsState($"hidden (editor={ViewModel?.IsEditorVisible}, fullscreen={ViewModel?.IsVideoFullscreen}, playback={_playback is not null})");
            }
            HideEditorHoverControls(immediate: true);
            return;
        }

        if (!GetCursorPos(out var cursor)) return;

        // EditorVideoHost, not EditorVideoView: the view carries the zoom
        // ScaleTransform (so its own PointToScreen moves and can extend well
        // outside the visible area once zoomed) and gets reparented into
        // FullscreenVideoHost and back. The host is the stable, untransformed
        // rectangle the video is actually shown in.
        if (EditorVideoHost.Bounds.Width <= 0 || EditorVideoHost.Bounds.Height <= 0) return;
        var videoTopLeft = EditorVideoHost.PointToScreen(new Point(0, 0));
        var videoBottomRight = EditorVideoHost.PointToScreen(new Point(EditorVideoHost.Bounds.Width, EditorVideoHost.Bounds.Height));
        var overVideo = cursor.X >= videoTopLeft.X && cursor.X < videoBottomRight.X
                        && cursor.Y >= videoTopLeft.Y && cursor.Y < videoBottomRight.Y;

        var overBar = false;
        if (_editorHoverControlsWindow is { IsVisible: true } existingBar)
        {
            var barPos = existingBar.Position;
            var barWidthPx = (int)(existingBar.Width * existingBar.RenderScaling);
            var barHeightPx = (int)(existingBar.Height * existingBar.RenderScaling);
            overBar = cursor.X >= barPos.X && cursor.X < barPos.X + barWidthPx
                      && cursor.Y >= barPos.Y && cursor.Y < barPos.Y + barHeightPx;
        }

        if (overVideo || overBar)
        {
            _hoverControlsActiveUntilUtc = DateTime.UtcNow + HoverControlsGrace;
            ShowEditorHoverControls();
        }
        else if (DateTime.UtcNow >= _hoverControlsActiveUntilUtc)
        {
            if (_editorHoverControlsWindow is { IsVisible: true } && !_hoverControlsSlidingOut)
            {
                LogHoverControlsState($"sliding out (cursor={cursor.X},{cursor.Y} video={videoTopLeft.X},{videoTopLeft.Y}-{videoBottomRight.X},{videoBottomRight.Y})");
            }
            HideEditorHoverControls(immediate: false);
        }
    }

    private void ShowEditorHoverControls()
    {
        _hoverControlsSlideOutTimer?.Stop();
        _hoverControlsSlidingOut = false;

        var window = EnsureEditorHoverControlsWindow();
        // Reposition only on the hidden->shown transition, not every poll
        // tick - repeatedly calling native SetWindowPos on a transparent/
        // composited window ~8x/sec while just sitting there hovering was
        // visibly janky. Actual repositioning while it's already visible
        // (window move/resize) is handled separately by the
        // PositionChanged/LayoutUpdated hooks in TrackPausedOverlayToWindow.
        if (!window.IsVisible)
        {
            RepositionEditorHoverControls(window);
            // Parked below the window's own bounds so the first frame after
            // Show is already off-screen; flipping it back one frame later
            // gives the transition a "from" state to animate out of, rather
            // than both values landing in the same layout pass.
            SetHoverControlsOffset(HoverControlsSlideDistance);
            window.Show(this);
            LogHoverControlsState("sliding in");
            Dispatcher.UIThread.Post(() => SetHoverControlsOffset(0), DispatcherPriority.Loaded);
        }
        else
        {
            SetHoverControlsOffset(0);
        }
    }

    private void HideEditorHoverControls(bool immediate)
    {
        if (_editorHoverControlsWindow is not { IsVisible: true } window)
        {
            _hoverControlsSlidingOut = false;
            return;
        }

        if (immediate)
        {
            _hoverControlsSlideOutTimer?.Stop();
            _hoverControlsSlidingOut = false;
            window.Hide();
            return;
        }

        if (_hoverControlsSlidingOut) return;
        _hoverControlsSlidingOut = true;
        SetHoverControlsOffset(HoverControlsSlideDistance);

        _hoverControlsSlideOutTimer ??= new DispatcherTimer();
        _hoverControlsSlideOutTimer.Interval = HoverControlsSlideDuration;
        _hoverControlsSlideOutTimer.Stop();
        _hoverControlsSlideOutTimer.Tick -= HoverControlsSlideOut_OnTick;
        _hoverControlsSlideOutTimer.Tick += HoverControlsSlideOut_OnTick;
        _hoverControlsSlideOutTimer.Start();
    }

    private void HoverControlsSlideOut_OnTick(object? sender, EventArgs e)
    {
        _hoverControlsSlideOutTimer?.Stop();
        if (!_hoverControlsSlidingOut) return;
        _hoverControlsSlidingOut = false;
        _editorHoverControlsWindow?.Hide();
        LogHoverControlsState("hidden");
    }

    private void SetHoverControlsOffset(double offset)
    {
        if (_hoverControlsBackdrop is null) return;
        _hoverControlsBackdrop.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(
            offset == 0 ? "translateY(0px)" : $"translateY({offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
    }

    private void RepositionEditorHoverControls(Window bar)
    {
        // Same reasoning as PollEditorHoverControls - position against the
        // untransformed host, not the zoom-transformed/reparented view.
        if (EditorVideoHost.Bounds.Width <= 0 || EditorVideoHost.Bounds.Height <= 0) return;
        var topLeft = EditorVideoHost.PointToScreen(new Point(0, 0));
        var width = EditorVideoHost.Bounds.Width;
        const double barHeight = 64;
        var bottomOnScreen = EditorVideoHost.PointToScreen(new Point(0, EditorVideoHost.Bounds.Height));
        bar.Width = Math.Max(1, width);
        bar.Height = barHeight;
        bar.Position = new PixelPoint(topLeft.X, bottomOnScreen.Y - (int)(barHeight * bar.RenderScaling));
    }

    private Window EnsureEditorHoverControlsWindow()
    {
        if (_editorHoverControlsWindow is not null) return _editorHoverControlsWindow;

        PathIcon Icon(string data, double size = 16) => new()
        {
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(Color.Parse("#C8D6E6")),
            Data = Geometry.Parse(data),
        };

        Button TransportButton(string data, EventHandler<RoutedEventArgs> onClick, string? tip = null)
        {
            var button = new Button { Classes = { "transportButton" }, Content = Icon(data) };
            button.Click += onClick;
            if (tip is not null) ToolTip.SetTip(button, tip);
            return button;
        }

        var playIcon = Icon("M8 5v14l11-7z", 16);
        playIcon.Foreground = new SolidColorBrush(Color.Parse("#DDE8F6"));
        playIcon.Bind(IsVisibleProperty, new Binding("!IsPlaying"));
        var pauseIcon = Icon("M6 19h4V5H6v14zm8-14v14h4V5h-4z", 16);
        pauseIcon.Foreground = new SolidColorBrush(Color.Parse("#DDE8F6"));
        pauseIcon.Bind(IsVisibleProperty, new Binding("IsPlaying"));
        var playPauseButton = new Button { Classes = { "playButton" }, Content = new Grid { Children = { playIcon, pauseIcon } } };
        playPauseButton.Click += PlayPauseButton_OnClick;

        var muteIcon = new PathIcon { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        muteIcon.Bind(PathIcon.ForegroundProperty, new Binding("IsMasterMuted") { Converter = BoolToMuteBrushConverter.Instance });
        muteIcon.Bind(PathIcon.DataProperty, new Binding("EffectiveMasterVolumePercent") { Converter = VolumeLevelToIconConverter.Instance });
        var muteToggle = new Border
        {
            Classes = { "muteToggle" },
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = muteIcon,
        };
        ToolTip.SetTip(muteToggle, "Mute/unmute");
        muteToggle.PointerPressed += MasterVolumeMuteToggle_OnPointerPressed;

        var volumeSlider = new Slider
        {
            Classes = { "volumeSlider" },
            Minimum = 0,
            Maximum = 150,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Width = 80,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volumeSlider.Bind(Slider.ValueProperty, new Binding("MasterVolumePercent", BindingMode.TwoWay));
        volumeSlider.Bind(OpacityProperty, new Binding("IsMasterMuted") { Converter = BoolToOpacityConverter.Instance });

        var timeText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#EDF4FB")), FontSize = 13, FontWeight = FontWeight.Bold, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        timeText.Bind(TextBlock.TextProperty, new Binding("CurrentTimeLabel"));
        var slashText = new TextBlock { Text = " / ", Foreground = new SolidColorBrush(Color.Parse("#5C6D7E")), FontSize = 13, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        var durationText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#8C98A7")), FontSize = 13, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        durationText.Bind(TextBlock.TextProperty, new Binding("DurationLabel"));

        var centerGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                TransportButton("M6 6h2v12H6zm3.5 6l8.5 6V6z", RestartButton_OnClick, "Restart"),
                TransportButton("M16 5v14L5 12Z", StepBackButton_OnClick, "Step back"),
                playPauseButton,
                TransportButton("M8 5v14l11-7z", StepForwardButton_OnClick, "Step forward"),
                TransportButton("M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z", EndButton_OnClick, "End"),
                new Border { Width = 1, Height = 20, Background = new SolidColorBrush(Color.Parse("#26FFFFFF")), Margin = new Thickness(4, 0) },
                new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { timeText, slashText, durationText } },
            },
        };

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { muteToggle, volumeSlider },
        };

        var fullscreenButton = TransportButton("M7,14H5v5h5v-2H7V14z M5,10h2V7h3V5H5V10z M17,17h-3v2h5v-5h-2V17z M14,5v2h3v3h2V5H14z", FullscreenButton_OnClick, "Fullscreen");
        fullscreenButton.HorizontalAlignment = HorizontalAlignment.Right;

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(14, 0),
        };
        Grid.SetColumn(leftGroup, 0);
        Grid.SetColumn(centerGroup, 1);
        Grid.SetColumn(fullscreenButton, 2);
        layout.Children.Add(leftGroup);
        layout.Children.Add(centerGroup);
        layout.Children.Add(fullscreenButton);

        var backdrop = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x08, 0x0B, 0x0E), 0),
                    new GradientStop(Color.FromArgb(0xE0, 0x08, 0x0B, 0x0E), 0.55),
                },
            },
            Child = layout,
            RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse($"translateY({HoverControlsSlideDistance}px)"),
            Transitions =
            [
                new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = HoverControlsSlideDuration,
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            ],
        };
        _hoverControlsBackdrop = backdrop;

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Topmost = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            DataContext = DataContext,
            Content = backdrop,
        };
        _editorHoverControlsWindow = window;
        return window;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct CursorPoint
    {
        public readonly int X;
        public readonly int Y;
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
        // Only auto-stops at TrimEnd for a play session that started at/before
        // it (see _trimEndGuardArmed) - a session explicitly started past
        // TrimEnd (user seeked there and hit play) is left alone so the
        // footage after the trim-out point can still be previewed.
        if (_trimEndGuardArmed && ViewModel.TrimEnd > TimeSpan.Zero && ViewModel.CurrentTime >= ViewModel.TrimEnd)
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
        _trimEndGuardArmed = ViewModel is null || ViewModel.TrimEnd <= TimeSpan.Zero || _playheadBaseTime < ViewModel.TrimEnd;
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
            Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-24.png"))),
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
