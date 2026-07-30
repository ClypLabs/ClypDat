using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using ClypDat.Capture.Abstractions;
using ClypDat.App.Controls;
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
    // Distance between where the pointer went down and the trim boundary it
    // grabbed. The handle's grab area is far wider than the line drawn in it,
    // so without carrying this offset through the drag, pressing anywhere but
    // dead-centre would snap the boundary to the pointer - moving the trim by
    // up to half the grab area before the drag even started, which is exactly
    // the precision the wider target is meant to provide.
    private double _trimDragGrabOffsetMs;
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
    private DispatcherTimer? _replayRestartDebounceTimer;
    private readonly EncoderTuningService _encoderTuning = new();
    private readonly SemaphoreSlim _clipSaveLock = new(1, 1);
    private bool _updateDialogOpen;
    private readonly ClipHoverPreviewController _clipHoverPreview = new();
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
    // Grace between the pointer leaving the video (and the bar) and the bar
    // going away. Short enough to still read as "leaves as soon as you do",
    // long enough to absorb a single stray poll tick. Zero was a mistake: the
    // poll runs at frame rate, and one tick that momentarily read as outside
    // started the slide-down under the pointer, so a click landed on a
    // control that had just moved and the bar looked like it flashed.
    private static readonly TimeSpan HoverControlsGrace = TimeSpan.FromMilliseconds(180);
    private DateTime _hoverControlsActiveUntilUtc = DateTime.MinValue;
    // While the window is being resized the bar is taken down entirely and
    // held down until this long after the last SizeChanged - long enough that
    // a continuous drag never lets it back up mid-resize, short enough not to
    // feel like a lag once the drag ends.
    private static readonly TimeSpan HoverControlsResizeSettle = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan LibraryResizeAnchorSettle = TimeSpan.FromMilliseconds(220);
    private const double LibraryScrollOffsetTolerance = 0.01;
    private DateTime _hoverControlsSuppressedUntilUtc = DateTime.MinValue;
    // Slides the bar in from under the video's bottom edge on first show. The
    // window itself stays put - only its content moves - because animating an
    // owned window's native position is visibly steppy next to a composited
    // transform.
    private const double HoverControlsSlideDistance = 92;
    private static readonly TimeSpan HoverControlsSlideDuration = TimeSpan.FromMilliseconds(190);
    private Border? _hoverControlsBackdrop;
    private DispatcherTimer? _hoverControlsSlideOutTimer;
    private bool _hoverControlsSlidingOut;
    private string? _libraryResizeAnchorPath;
    private bool _libraryResizeAnchorRestoreQueued;
    private DispatcherTimer? _libraryResizeAnchorSettleTimer;
    private int _libraryResizeAnchorGeneration;
    private double? _libraryResizeExpectedOffsetY;
    // Separate from the drag-resize anchor above: captured right before
    // opening a clip into the editor (OpenClipCardAsync), and restored on
    // the way back if the window was resized while away.
    // CaptureLibraryResizeAnchor bails out whenever Library isn't visible,
    // so a resize taken entirely inside the editor previously left
    // LibraryScrollViewer's Offset.Y pointing at pixels from the OLD
    // cards-per-row layout once Library became visible (and re-measured
    // with the new width) again.
    private string? _libraryReturnAnchorPath;
    private bool _libraryReturnAnchorDirty;
    private const double ScrollToTopButtonThreshold = 320;
    private static readonly TimeSpan ScrollToTopDuration = TimeSpan.FromMilliseconds(380);
    public MainWindow()
    {
        InitializeComponent();
        EditorVideoView.VideoClicked += (_, _) => PlayPauseButton_OnClick(this, new RoutedEventArgs());
        // ApplySavedWindowBounds can restore straight into Maximized, which
        // won't raise an OffScreenMargin change of its own.
        RootLayout.Margin = OffScreenMargin;
        UpdateViewNavButtons();
        LibraryScrollViewer.ScrollChanged += LibraryScrollViewer_OnScrollChanged;
        // Card layout follows the grid's real width, not the window's - the
        // sidebar rail and date scrubber both sit outside this ScrollViewer.
        LibraryScrollViewer.SizeChanged += (_, sizeArgs) => ViewModel?.UpdateCardLayout(sizeArgs.NewSize.Width);
        // Card visibility flips (filtering) and hydration both change the
        // scroll extent without a size change on this window, so the marker
        // positions have to be recomputed off layout rather than only off
        // Window_OnSizeChanged.
        LibraryScrollViewer.LayoutUpdated += (_, _) => QueueDateScrubberRebuild();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        // Guarded like the hover-bar poll timer below (see SetupEditorHoverControls) -
        // SyncPlaybackPosition repositions the "Playback Paused" badge via
        // EditorVideoView.PointToScreen, which can throw while the view is
        // momentarily detached (the fullscreen reparent). An unguarded throw here
        // kills this tick subscription for the rest of the session at 60fps odds
        // of hitting that window, which is what made the badge (and everything
        // else this timer drives) vanish permanently instead of just skipping a beat.
        _playbackTimer.Tick += (_, _) =>
        {
            try
            {
                SyncPlaybackPosition();
            }
            catch (Exception error)
            {
                AppLog.Error("Playback position sync failed (recovered)", error);
            }
        };
        _gameDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameDetectionTimer.Tick += (_, _) => UpdateDetectedGame();
        Opened += (_, _) =>
        {
            // Card layout comes from LibraryScrollViewer's own SizeChanged
            // (wired above) - at Opened its width may still be 0.
            ClearLibraryResizeAnchor();
            LibraryScrollViewer.Offset = default;
            InitializeReplayServices();
            UpdateDetectedGame();
            _gameDetectionTimer.Start();
            _ = EnsureLibraryFolderAsync();
            // Four independent HTTPS calls used to fire in the same instant here.
            // At logon the network stack is often not up yet, so each one can
            // sit in its own DNS/TLS timeout concurrently - staggering them
            // costs nothing (none of this gates the UI) and avoids piling that
            // wait onto the exact moment everything else is also starting up.
            _ = RunStartupDialogsAsync();
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => RefreshRemoteGameIconsAsync(), TaskScheduler.Default);
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => RefreshRemoteGameCatalogAsync(), TaskScheduler.Default);
            if (ViewModel is not null)
            {
                _gameDetector.ApplyCustomGameNames(ViewModel.Settings.GameCaptureOverrides);
                _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
                ViewModel.GameCatalogChanged += (_, _) =>
                {
                    _gameDetector.ApplyCustomGameNames(ViewModel.Settings.GameCaptureOverrides);
                    _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
                };
                ViewModel.ClipAdded += ViewModel_OnClipAdded;
                ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.AutoClippingEnabled)) UpdateAutoClipStates();
                    if (e.PropertyName == nameof(MainWindowViewModel.ReplayBufferEnabled)) _ = ApplyReplayBufferEnabledAsync();
                    if (e.PropertyName is nameof(MainWindowViewModel.MasterVolumePercent) or nameof(MainWindowViewModel.IsMasterMuted)) _playback?.SetMasterVolume(ViewModel.EffectiveMasterVolumePercent);
                    if (e.PropertyName is nameof(MainWindowViewModel.VideoZoom) or nameof(MainWindowViewModel.VideoPanY)) UpdateVideoTransform();
                    if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsVisible) && ViewModel.IsSettingsVisible) PauseEditorPlayback();
                    if (e.PropertyName == nameof(MainWindowViewModel.EnableClipHoverPreview) && !ViewModel.EnableClipHoverPreview) _clipHoverPreview.Stop("setting disabled");
                    if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsVisible) or nameof(MainWindowViewModel.IsEditorVisible))
                    {
                        if (!ViewModel.IsLibraryVisible) _clipHoverPreview.Stop("navigation");
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.ReplayQualityRestartRequired) && ViewModel.ReplayQualityRestartRequired)
                    {
                        // Debounced rather than restarting on every keystroke/click -
                        // a user dragging through resolutions or typing a bitrate
                        // digit by digit shouldn't tear the buffer down each time.
                        _replayRestartDebounceTimer?.Stop();
                        _replayRestartDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        _replayRestartDebounceTimer.Tick += async (_, _) =>
                        {
                            _replayRestartDebounceTimer?.Stop();
                            _replayRestartDebounceTimer = null;
                            if (ViewModel is { IsReplayRecording: true, ReplayQualityRestartRequired: true })
                            {
                                await StopReplayBufferAsync();
                                await StartReplayBufferAsync(showErrors: true, isQualityRestart: true);
                            }
                        };
                        _replayRestartDebounceTimer.Start();
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsEditorVisible) && ViewModel.IsLibraryVisible && _libraryReturnAnchorDirty)
                    {
                        _libraryReturnAnchorDirty = false;
                        var anchorPath = _libraryReturnAnchorPath;
                        // Loaded priority so this runs after the layout pass that
                        // finally re-measures LibraryScrollViewer/the WrapPanel at
                        // its current width - it was collapsed (no layout at all)
                        // for the whole time the editor was open, so the resize
                        // that happened during that window only takes effect now.
                        Dispatcher.UIThread.Post(() => RestoreLibraryResizeAnchor(anchorPath), DispatcherPriority.Loaded);
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsRestoringLibraryCache) && !ViewModel.IsRestoringLibraryCache)
                    {
                        // Adding cached cards in low-priority batches grows the
                        // WrapPanel several times during first layout. Avalonia
                        // can preserve a transient child offset through those
                        // extent changes, leaving a fresh library slightly
                        // below its real top. A cache restore is initial/root
                        // navigation, so its final position is always top.
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ViewModel?.IsRestoringLibraryCache == true) return;
                            ClearLibraryResizeAnchor();
                            LibraryScrollViewer.Offset = default;
                        }, DispatcherPriority.Loaded);
                    }
                    if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsVisible)
                        or nameof(MainWindowViewModel.IsEditorVisible)
                        or nameof(MainWindowViewModel.SelectedVideoPath)
                        or nameof(MainWindowViewModel.IsGameFilterActive)
                        or nameof(MainWindowViewModel.IsClipTypeFilterActive)) OnViewHistoryStateChanged();
                };
                foreach (var autoClipGame in ViewModel.AutoClipGames)
                {
                    autoClipGame.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(AutoClipGameViewModel.IsEnabled)) UpdateAutoClipStates();
                    };
                }
                // TryDeploy for CS2/Dota does registry reads, Steam library
                // enumeration and file IO synchronously - fine on demand, but
                // running it inline in Opened blocked the window's first paint
                // on it. Posted so the window shows first; still on the UI
                // thread (these touch bound StatusText/listener state), just
                // not gating Opened itself.
                Dispatcher.UIThread.Post(UpdateAutoClipStates, DispatcherPriority.Background);
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
        // Owned windows (the hover bar, the paused badge) can get hidden by
        // Windows itself - owner minimized (alt-tab into an exclusive-
        // fullscreen game), owner loses foreground (alt-tab away, clicking
        // another window), even a transient focus blip mid interactive-resize
        // - none of which goes through our own Hide() calls, so Avalonia's
        // window.IsVisible for them goes stale (still true) while the native
        // window is actually gone. ShowEditorHoverControls only calls Show()
        // again when IsVisible reads false, so a stale true meant the bar
        // never came back - "vanishes, never returns" on exactly resize/
        // alt-tab/unfocus. Force both back to a known-good hidden state on
        // either side of a focus transition so the next poll tick (which
        // reads IsVisible honestly false now) re-shows the bar correctly
        // instead of trusting state the OS already invalidated behind us.
        // No Activated/Deactivated hiding here on purpose. Resyncing the
        // overlays on focus changes was an attempt at the stale-IsVisible bug
        // that PollEditorHoverControls' IsWindowVisible check now handles
        // properly and directly. Keeping both meant any focus change - including
        // one caused by clicking the bar itself - yanked the bar out from under
        // the pointer mid-click.
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
                StopEditorPlayback(stopMode: PlaybackStopMode.Background);
                Hide();
                ShowInTaskbar = false;
            }
        };
        Closed += (_, _) =>
        {
            _clipHoverPreview.Dispose();
            _libraryResizeAnchorSettleTimer?.Stop();
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
            EditorVideoView.DisposeClickHandling();
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
        // Game-rail drag - same reasoning as KeyDown/KeyUp above: a plain
        // (bubble) PointerPressed wired directly on a rail Button never saw
        // the press at all, because Button's OWN internal press handling (it
        // captures the pointer and tracks click state) runs first and marks
        // the event Handled before a same-element bubble handler gets a
        // turn. Tunnel runs top-down BEFORE that, so it sees every press
        // regardless of what the Button goes on to do with it.
        AddHandler(PointerPressedEvent, GameRailItem_OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, GameRailItem_OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, GameRailItem_OnPointerReleased, RoutingStrategies.Tunnel);

        // Change Game's flyout (see ClipContextSetGame_OnClick) never
        // light-dismissed on an outside click on its own, through three
        // different popup approaches - something about this window's own
        // input handling swallows whatever signal a Popup normally listens
        // for. A popup renders on its own separate surface, so any pointer
        // press that reaches THIS window's own pipeline at all necessarily
        // landed outside it - closing on every such press, rather than
        // trusting the popup to notice on its own, sidesteps the problem
        // entirely instead of chasing why light-dismiss itself won't fire.
        AddHandler(PointerPressedEvent, (_, _) => _changeGameFlyout?.Hide(), RoutingStrategies.Tunnel);
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

        // Cheap per-window resolution now (see ForegroundGameDetector), but no
        // reason to poll as fast while nothing is running - back off to 3s with
        // no game present, snap back to 1s the moment one shows up so capture
        // still starts promptly.
        _gameDetectionTimer.Interval = TimeSpan.FromSeconds(detection.IsDetected ? 1 : 3);

        HarvestGameIcons();

        if (detection.IsDetected && ViewModel.Settings.ReplayBufferEnabled && _replayBuffer is { IsRecording: false } && !_replayTransitioning)
        {
            _ = StartReplayBufferAsync(showErrors: false);
        }
        else if (_replayBuffer is { IsRecording: true } && !detection.IsDetected && !_replayTransitioning)
        {
            // The game just closed. Show what the session captured once the
            // buffer has actually stopped - not before, or a clip saved in the
            // last seconds could still be mid-write.
            _ = StopReplayBufferAsync().ContinueWith(
                _ =>
                {
                    ShowNewClipsDialog();
                    // The ring buffer (the single biggest deliberate
                    // allocation in the app - real duration x bitrate, always
                    // live in RAM while armed, see NativeReplayBuffer) was just
                    // freed by the stop above and nothing is capturing right
                    // now to make a GC pause costly. Left to its own
                    // heuristics the CLR is content to hold that freed memory
                    // in reserve rather than return it to the OS, which is
                    // exactly the "still shows a lot of RAM after I stopped
                    // recording" complaint. A background, non-blocking
                    // collection here asks for it back at the one moment
                    // that's actually free - NOT done on every quality
                    // restart's Stop+Start, which frees the same memory only
                    // to immediately reallocate it.
                    Task.Run(() => GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true));
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.FromCurrentSynchronizationContext());
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

    // Header menu lets user exclude a detected game.
    private void ActiveGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is null) return;
        var detection = ViewModel.ActiveGameDetection;
        var detectionKey = string.IsNullOrWhiteSpace(detection.DetectionKey) ? detection.ExeName : detection.DetectionKey;
        if (detection is not { IsDetected: true } || string.IsNullOrWhiteSpace(detectionKey)) return;

        var flyout = new MenuFlyout();
        var exclude = new MenuItem
        {
            Header = new TextBlock
            {
                // detectionKey is what actually gets ignored (it's how Steam/Epic/
                // BattleNet/Riot matches get grouped - see ForegroundGameDetector's
                // GroupBy), but it is an internal id, not something to show a user:
                // "steam-548430", "epic-somenormalizedname", "battlenet-...",
                // "riot-..." depending on source. ExeName is what a player actually
                // recognises the game by, so that's what the menu item shows -
                // detectionKey itself is unchanged below, so the ignore still
                // groups correctly regardless of which name is on screen.
                Text = $"Don't detect \"{detection.DisplayName}\" ({detection.ExeName}) as a game",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            }
        };
        exclude.Click += (_, _) =>
        {
            ViewModel.AddIgnoredGameExecutable(detectionKey);
            _gameDetector.ApplyUserIgnoredExecutables(ViewModel.Settings.IgnoredGameExecutables);
            UpdateDetectedGame();
        };
        flyout.Items.Add(exclude);
        flyout.ShowAt(button);
    }

    private void RemoveIgnoredGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IgnoredGameRowViewModel row } || ViewModel is null) return;
        var executableName = row.Key;
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

    // Observe-only for now: EncoderTuningService logs the preset change it WOULD
    // make and never touches a setting. See its own notes for why it is being
    // run against real sessions before it is allowed to act on anything.
    private void AttachEncoderTuning(IReplayBuffer buffer)
    {
        if (buffer is IReplayCaptureDiagnostics diagnostics)
        {
            diagnostics.HealthChanged += EncoderTuning_OnHealthChanged;
        }
    }

    private void EncoderTuning_OnHealthChanged(object? sender, ReplayCaptureHealth health) => _encoderTuning.OnHealth(health);

    private void InitializeReplayServices()
    {
        if (ViewModel is null || _replayBuffer is not null) return;

        _replayBuffer = ReplayBufferFactory.Create(ViewModel.CreateReplayConfig);
        _replayBuffer.RecordingStopped += ReplayBuffer_OnRecordingStopped;
        AttachEncoderTuning(_replayBuffer);
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

        ViewModel.RecorderStatus = ReplayIdleStatus;
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
        if (_replayBuffer is IReplayCaptureDiagnostics oldDiagnostics) oldDiagnostics.HealthChanged -= EncoderTuning_OnHealthChanged;
        _replayBuffer.Dispose();
        _replayBuffer = ReplayBufferFactory.Create(ViewModel.CreateReplayConfig);
        _replayBuffer.RecordingStopped += ReplayBuffer_OnRecordingStopped;
        AttachEncoderTuning(_replayBuffer);
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

    // ---- Sidebar game rail: folders, ordering, drag/drop -------------------
    //
    // Drag is fully manual pointer-tracking, not Avalonia's DragDrop.DoDragDrop
    // (tried first - never actually engaged when started from inside a
    // Button's own PointerMoved, apparently losing the gesture to the
    // Button's own internal press/capture handling before the native OS drag
    // ever got going). This instead just captures the pointer to the source
    // control on press-and-move, hit-tests whatever's actually under the
    // cursor on every subsequent move via InputHitTest, and applies the move
    // directly on release - no native drag session, no DataObject, entirely
    // Avalonia's own input pipeline start to finish.
    //
    // Drop zones, based on where inside the TARGET tile the release lands:
    // the top ~30% inserts the source before it, the bottom ~30% after it,
    // and the middle ~40% - the bulk of the tile - merges the two into a
    // folder (or files into one, if the target already is a folder). A
    // dragged FOLDER never merges (folders don't nest), so for a folder
    // source the split is just top-half/bottom-half, always a reorder.

    private enum GameDropZone { Before, After, Merge }

    private Point? _gameDragStartPoint;
    private Control? _gameDragCandidate;
    private bool _gameDragActive;
    private string? _gameDragToken;
    private Control? _gameDragSourceControl;
    private Control? _gameDragTargetControl;
    private GameDropZone _gameDragTargetZone;
    private Image? _gameDragGhost;
    private const double GameDragThreshold = 6;

    // Registered at the window level (Tunnel - see the constructor) rather
    // than per-button, so "sender" here is always the window; the actual
    // rail tile, if any, comes from walking up from e.Source (the specific
    // PathIcon/Image/TextBlock the press actually landed on).
    private void GameRailItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var tile = FindRailTileAncestor(e.Source as Control);
        if (tile is null) return;

        _gameDragCandidate = tile;
        _gameDragStartPoint = e.GetPosition(this);
    }

    private void GameRailItem_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gameDragActive)
        {
            // The pointer is captured to the control the drag started on, so
            // this SAME handler keeps firing for every subsequent move no
            // matter what the cursor is actually over now - that's what makes
            // hit-testing against the live cursor position necessary here,
            // rather than trusting where the event says it originated.
            var point = e.GetPosition(this);
            UpdateGameDragTarget(point);
            PositionGameDragGhost(point);
            return;
        }

        if (_gameDragCandidate is null || _gameDragStartPoint is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _gameDragCandidate = null;
            _gameDragStartPoint = null;
            return;
        }

        var delta = e.GetPosition(this) - _gameDragStartPoint.Value;
        if (Math.Abs(delta.X) < GameDragThreshold && Math.Abs(delta.Y) < GameDragThreshold) return;

        var token = GameRailTokenFor(_gameDragCandidate);
        if (token is null)
        {
            _gameDragCandidate = null;
            _gameDragStartPoint = null;
            return;
        }

        _gameDragActive = true;
        _gameDragToken = token;
        _gameDragSourceControl = _gameDragCandidate;
        _gameDragCandidate = null;
        _gameDragStartPoint = null;

        // Snapshotted BEFORE dimming, so the floating ghost that follows the
        // cursor shows the tile at full brightness rather than doubly-faded
        // (once from this snapshot, again from the ghost's own opacity).
        _gameDragGhost = CreateGameDragGhost(_gameDragSourceControl);

        // Dimmed noticeably (not just hidden), so there's clear confirmation
        // something is actually being picked up - the previous 0.45 read as
        // barely different from resting.
        _gameDragSourceControl.Opacity = 0.3;
        Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(_gameDragSourceControl);
        e.Handled = true;

        var startPoint = e.GetPosition(this);
        UpdateGameDragTarget(startPoint);
        PositionGameDragGhost(startPoint);
    }

    // A live VisualBrush of the source tile would keep reflecting its
    // Opacity as the drag proceeds (dimmed to 0.3 right after this runs),
    // so the ghost is a static bitmap snapshot taken now, while the tile is
    // still at full brightness.
    private static Image? CreateGameDragGhost(Control source)
    {
        var bounds = source.Bounds;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var snapshot = new RenderTargetBitmap(new PixelSize(width, height));
        snapshot.Render(source);

        var overlay = OverlayLayer.GetOverlayLayer(source);
        if (overlay is null) return null;

        var ghost = new Image
        {
            Source = snapshot,
            Width = bounds.Width,
            Height = bounds.Height,
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        overlay.Children.Add(ghost);
        return ghost;
    }

    private void PositionGameDragGhost(Point windowPoint)
    {
        if (_gameDragGhost is null) return;
        Canvas.SetLeft(_gameDragGhost, windowPoint.X - _gameDragGhost.Width / 2);
        Canvas.SetTop(_gameDragGhost, windowPoint.Y - _gameDragGhost.Height / 2);
    }

    private void RemoveGameDragGhost()
    {
        if (_gameDragGhost is null) return;
        var overlay = OverlayLayer.GetOverlayLayer(this);
        overlay?.Children.Remove(_gameDragGhost);
        _gameDragGhost = null;
    }

    private void GameRailItem_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gameDragActive)
        {
            e.Pointer.Capture(null);
            FinishGameDrag(e.GetPosition(this));
            e.Handled = true;
        }

        _gameDragCandidate = null;
        _gameDragStartPoint = null;
    }

    // The reorder/merge target the drag will land on - a bar on the target
    // tile's top or bottom edge for a reorder, a ring around the whole tile
    // for a merge/file-into (the floating ghost, see CreateGameDragGhost,
    // only shows what's being carried - this highlight is still what says
    // where it'll land). Only touches styling when the target or zone has
    // actually changed, so this can run on every pointer move without
    // constantly rewriting the same values.
    private void UpdateGameDragTarget(Point windowPoint)
    {
        var hit = this.InputHitTest(windowPoint) as Control;
        var target = FindRailTileAncestor(hit);
        if (target is not null && ReferenceEquals(target, _gameDragSourceControl)) target = null;

        // A direct hit-test only resolves when the cursor is literally over
        // another tile's bounds - the gap between tiles, or past the top/
        // bottom of the rail, used to leave the target unresolved and the
        // drag looking like it wasn't doing anything. Falling back to the
        // nearest tile by vertical distance (the rail is a single column,
        // so Y alone identifies the right neighbor) makes a reorder register
        // anywhere near the rail. Bounded to the rail's own horizontal
        // extent so a drag that's left the sidebar entirely (over the main
        // content pane, say) still correctly resolves to "no target" instead
        // of always snapping to whichever rail tile is nearest vertically.
        if (target is null && IsWithinGameRailBounds(windowPoint))
        {
            target = FindNearestGameRailTile(windowPoint);
        }

        var zone = target is null ? GameDropZone.Before : ComputeDropZone(target, windowPoint);
        if (ReferenceEquals(target, _gameDragTargetControl) && zone == _gameDragTargetZone) return;

        ClearGameDragHighlight(_gameDragTargetControl);
        _gameDragTargetControl = target;
        _gameDragTargetZone = zone;
        if (target is not null) ApplyGameDragHighlight(target, zone);
    }

    // Horizontal containment only, deliberately - the nearest-tile fallback
    // below already handles a cursor above the top tile or below the bottom
    // one by distance, so the only thing worth gating here is a drag that's
    // left the rail's COLUMN entirely (e.g. drifted over the main content
    // pane), which shouldn't snap to whatever rail tile happens to be
    // nearest vertically.
    private bool IsWithinGameRailBounds(Point windowPoint)
    {
        var topLeft = GameRailContainer.TranslatePoint(new Point(0, 0), this);
        if (topLeft is null) return false;
        return windowPoint.X >= topLeft.Value.X && windowPoint.X <= topLeft.Value.X + GameRailContainer.Bounds.Width;
    }

    private Control? FindNearestGameRailTile(Point windowPoint)
    {
        Control? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var candidate in this.GetVisualDescendants().OfType<Button>())
        {
            if (candidate.DataContext is not (FilterOptionViewModel or GameRailFolderViewModel)) continue;
            if (ReferenceEquals(candidate, _gameDragSourceControl)) continue;

            var topLeft = candidate.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null) continue;
            var bounds = new Rect(topLeft.Value, candidate.Bounds.Size);

            var distance = windowPoint.Y < bounds.Top ? bounds.Top - windowPoint.Y
                : windowPoint.Y > bounds.Bottom ? windowPoint.Y - bounds.Bottom
                : 0;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }
        return nearest;
    }

    private static IBrush GameDragAccentBrush => Application.Current?.Resources["AccentBrush"] as IBrush ?? Brushes.CornflowerBlue;

    private static void ApplyGameDragHighlight(Control target, GameDropZone zone)
    {
        if (target is not Button button) return;
        button.BorderBrush = GameDragAccentBrush;
        button.BorderThickness = zone switch
        {
            GameDropZone.Before => new Thickness(0, 3, 0, 0),
            GameDropZone.After => new Thickness(0, 0, 0, 3),
            _ => new Thickness(2),
        };
    }

    // ClearValue rather than setting a hardcoded "off" value - restores
    // whatever railIconButton's own style would normally supply (0 thickness,
    // no explicit brush) instead of assuming what that is.
    private static void ClearGameDragHighlight(Control? target)
    {
        if (target is not Button button) return;
        button.ClearValue(Button.BorderBrushProperty);
        button.ClearValue(Button.BorderThicknessProperty);
    }

    private void FinishGameDrag(Point releasePoint)
    {
        var sourceToken = _gameDragToken;
        var sourceControl = _gameDragSourceControl;
        var targetControl = _gameDragTargetControl;
        var zone = _gameDragTargetZone;

        ClearGameDragHighlight(targetControl);
        Cursor = Cursor.Default;
        RemoveGameDragGhost();

        _gameDragActive = false;
        _gameDragToken = null;
        _gameDragSourceControl = null;
        _gameDragTargetControl = null;

        if (sourceControl is not null) sourceControl.Opacity = 1;
        if (ViewModel is null || sourceToken is null) return;

        if (targetControl is not null)
        {
            ApplyGameDrag(sourceToken, targetControl, zone);
            return;
        }

        // No target under the cursor at release. If that's because the
        // pointer never really left the source (dragged out and back), treat
        // it as a cancel rather than shoving the game to the end of the rail
        // for what amounted to no real move.
        if (sourceControl is not null && IsPointWithin(sourceControl, releasePoint)) return;
        ApplyGameDragToEnd(sourceToken);
    }

    private bool IsPointWithin(Control control, Point windowPoint)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), this);
        if (topLeft is null) return false;
        return new Rect(topLeft.Value, control.Bounds.Size).Contains(windowPoint);
    }

    // Where in TARGET's own bounds windowPoint falls, top to bottom - see the
    // region comment above for what each third means.
    private GameDropZone ComputeDropZone(Control target, Point windowPoint)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), this) ?? default;
        var height = target.Bounds.Height;
        var fraction = height > 0 ? (windowPoint.Y - topLeft.Y) / height : 0.5;

        var draggingFolder = _gameDragToken is not null && _gameDragToken.StartsWith("folder:", StringComparison.Ordinal);
        if (draggingFolder) return fraction <= 0.5 ? GameDropZone.Before : GameDropZone.After;

        // A folder target gets wider Before/After margins than a loose game
        // does - reordering a game past an EXISTING folder is the far more
        // common intent than merging into it, but the tight 30% margins
        // (about 13px on a 44px tile) made landing a reorder past a folder
        // fiddly, tipping into "merge into folder" more often than intended.
        var targetIsFolder = target.DataContext is GameRailFolderViewModel;
        var margin = targetIsFolder ? 0.4 : 0.3;
        if (fraction < margin) return GameDropZone.Before;
        if (fraction > 1 - margin) return GameDropZone.After;
        return GameDropZone.Merge;
    }

    private static Control? FindRailTileAncestor(Control? control)
    {
        var current = control;
        while (current is not null)
        {
            if (current is Button && current.DataContext is FilterOptionViewModel or GameRailFolderViewModel) return current;
            current = current.GetVisualParent() as Control;
        }
        return null;
    }

    private void ApplyGameDrag(string sourceToken, Control targetControl, GameDropZone zone)
    {
        if (ViewModel is null) return;

        if (targetControl.DataContext is GameRailFolderViewModel targetFolder)
        {
            if (TryParseGameRailGameToken(sourceToken, out var sourceGameKey))
            {
                // Only the middle band ("Merge") means "file into me" - top/
                // bottom now reorder the game as a top-level entry relative
                // to the folder instead, same as dropping it near a loose
                // game already does. RelocateGame has no "before this
                // FOLDER" targeting of its own, so this moves the game to
                // top level first (also handles removing it from wherever
                // it currently lives, including a different folder) and
                // then repositions that top-level token precisely.
                if (zone == GameDropZone.Merge)
                {
                    ViewModel.RelocateGame(sourceGameKey, targetFolder.Id, null);
                }
                else
                {
                    var beforeToken = zone == GameDropZone.After ? NextTopLevelTokenAfter("folder:" + targetFolder.Id) : "folder:" + targetFolder.Id;
                    ViewModel.RelocateGame(sourceGameKey, null, null);
                    ViewModel.RelocateTopLevelEntry("game:" + sourceGameKey, beforeToken);
                }
            }
            else if (TryParseGameRailFolderToken(sourceToken, out var sourceFolderId) && !string.Equals(sourceFolderId, targetFolder.Id, StringComparison.OrdinalIgnoreCase))
            {
                var beforeToken = zone == GameDropZone.After ? NextTopLevelTokenAfter("folder:" + targetFolder.Id) : "folder:" + targetFolder.Id;
                ViewModel.RelocateTopLevelEntry("folder:" + sourceFolderId, beforeToken);
            }
            return;
        }

        if (targetControl.DataContext is not FilterOptionViewModel targetGame) return;
        var enclosingFolder = FindEnclosingFolder(targetControl);

        if (TryParseGameRailFolderToken(sourceToken, out var draggedFolderId))
        {
            // Folders only ever reorder among top-level entries - dropping one
            // on a game that's inside another folder has nowhere sensible to
            // land (folders don't nest), so it's ignored.
            if (enclosingFolder is not null) return;
            var beforeToken = zone == GameDropZone.After ? NextTopLevelTokenAfter("game:" + targetGame.Key) : "game:" + targetGame.Key;
            ViewModel.RelocateTopLevelEntry("folder:" + draggedFolderId, beforeToken);
            return;
        }

        if (!TryParseGameRailGameToken(sourceToken, out var sourceKey)) return;
        if (string.Equals(sourceKey, targetGame.Key, StringComparison.OrdinalIgnoreCase)) return;

        switch (zone)
        {
            case GameDropZone.Merge when enclosingFolder is not null:
                // Target already belongs to a folder - join it there instead
                // of trying to nest a second folder inside it.
                ViewModel.RelocateGame(sourceKey, enclosingFolder.Id, null);
                break;
            case GameDropZone.Merge:
                // Anchored on the TARGET (goes first into the new folder) -
                // dropping A onto B should read as "B absorbed A", landing
                // where B was, not where A came from.
                ViewModel.CreateGameFolder(new[] { targetGame.Key, sourceKey });
                break;
            case GameDropZone.Before:
                ViewModel.RelocateGame(sourceKey, enclosingFolder?.Id, targetGame.Key);
                break;
            case GameDropZone.After:
                var afterKey = enclosingFolder is null
                    ? NextTopLevelGameKeyAfter(targetGame.Key)
                    : NextFolderGameKeyAfter(enclosingFolder, targetGame.Key);
                ViewModel.RelocateGame(sourceKey, enclosingFolder?.Id, afterKey);
                break;
        }
    }

    private void ApplyGameDragToEnd(string sourceToken)
    {
        if (ViewModel is null) return;
        if (TryParseGameRailGameToken(sourceToken, out var sourceKey))
        {
            ViewModel.RelocateGame(sourceKey, destinationFolderId: null, beforeGameKey: null);
        }
        else if (TryParseGameRailFolderToken(sourceToken, out _))
        {
            ViewModel.RelocateTopLevelEntry(sourceToken, beforeToken: null);
        }
    }

    // The top-level token (game or folder) rendered immediately after the
    // given one, or null if it's last - used to express "insert AFTER target"
    // in terms RelocateTopLevelEntry already understands ("insert before X").
    private string? NextTopLevelTokenAfter(string token)
    {
        if (ViewModel is null) return null;
        var entries = ViewModel.GameRailEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(GameRailTokenForEntry(entries[i]), token, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < entries.Count ? GameRailTokenForEntry(entries[i + 1]) : null;
            }
        }
        return null;
    }

    // Same idea for a GAME landing at the top level specifically - null when
    // the target is last, or when the very next entry is a folder rather than
    // a game (RelocateGame's beforeGameKey only understands games), in which
    // case the source just appends at the end instead of slotting exactly
    // between the two. A minor imprecision in that one case, not worth a
    // bigger API for.
    private string? NextTopLevelGameKeyAfter(string gameKey)
    {
        if (ViewModel is null) return null;
        var entries = ViewModel.GameRailEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is FilterOptionViewModel game && string.Equals(game.Key, gameKey, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < entries.Count && entries[i + 1] is FilterOptionViewModel next ? next.Key : null;
            }
        }
        return null;
    }

    // Within a folder every entry is a game, so this one's always exact.
    private static string? NextFolderGameKeyAfter(GameRailFolderViewModel folder, string gameKey)
    {
        for (var i = 0; i < folder.Games.Count; i++)
        {
            if (string.Equals(folder.Games[i].Key, gameKey, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < folder.Games.Count ? folder.Games[i + 1].Key : null;
            }
        }
        return null;
    }

    private static string? GameRailTokenForEntry(object entry) => entry switch
    {
        FilterOptionViewModel game => "game:" + game.Key,
        GameRailFolderViewModel folder => "folder:" + folder.Id,
        _ => null
    };

    private static string? GameRailTokenFor(Control control) => GameRailTokenForEntry(control.DataContext!);

    private static bool TryParseGameRailGameToken(string token, out string key)
    {
        if (token.StartsWith("game:", StringComparison.Ordinal))
        {
            key = token["game:".Length..];
            return true;
        }
        key = string.Empty;
        return false;
    }

    private static bool TryParseGameRailFolderToken(string token, out string id)
    {
        if (token.StartsWith("folder:", StringComparison.Ordinal))
        {
            id = token["folder:".Length..];
            return true;
        }
        id = string.Empty;
        return false;
    }

    // Walks up from a rail control to find the folder it's rendered inside
    // (null for anything at the top level) - how a drop target's own
    // location in the rail is determined, regardless of how deep the
    // DataTemplate nesting that put it there actually is.
    private static GameRailFolderViewModel? FindEnclosingFolder(Control control)
    {
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is Control ancestorControl && ancestorControl.DataContext is GameRailFolderViewModel folder) return folder;
        }
        return null;
    }

    private void GameRailFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameRailFolderViewModel folder) folder.ToggleExpanded();
    }

    // Rebuilds the dynamic parts of a game's context menu right before it
    // shows: the "Move to folder" submenu (which folders exist changes
    // constantly) and whether "Remove from folder" applies to this
    // particular game at all.
    private void GameContextMenu_OnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not ContextMenu contextMenu) return;
        if (contextMenu.PlacementTarget?.DataContext is not FilterOptionViewModel option) return;

        var currentFolderId = ViewModel.FindContainingFolderId(option.Key);

        if (contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "MoveToFolder")) is { } moveSubmenu)
        {
            moveSubmenu.Items.Clear();

            var newFolder = new MenuItem { Header = "New Folder..." };
            newFolder.Click += async (_, _) => await CreateGameFolderViaPromptAsync(option.Key);
            moveSubmenu.Items.Add(newFolder);

            var otherFolders = ViewModel.GameRailEntries.OfType<GameRailFolderViewModel>()
                .Where(folder => !string.Equals(folder.Id, currentFolderId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (otherFolders.Length > 0) moveSubmenu.Items.Add(new Separator());
            foreach (var folder in otherFolders)
            {
                var item = new MenuItem { Header = folder.Name };
                var folderId = folder.Id;
                item.Click += (_, _) => ViewModel.RelocateGame(option.Key, folderId, null);
                moveSubmenu.Items.Add(item);
            }
        }

        if (contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "RemoveFromFolder")) is { } removeItem)
        {
            removeItem.IsVisible = currentFolderId is not null;
        }
    }

    private async Task CreateGameFolderViaPromptAsync(string gameKey)
    {
        if (ViewModel is null) return;
        var name = await PromptRenameAsync(gameKey, "New folder", "Folder name");
        if (string.IsNullOrWhiteSpace(name)) return;
        ViewModel.CreateGameFolder(new[] { gameKey }, name);
    }

    private void RemoveGameFromFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as Control)?.DataContext is not FilterOptionViewModel option) return;
        ViewModel.RelocateGame(option.Key, destinationFolderId: null, beforeGameKey: null);
    }

    private async void RenameFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as Control)?.DataContext is not GameRailFolderViewModel folder) return;
        var newName = await PromptRenameAsync(folder.Name, "Rename folder", "Folder name");
        if (string.IsNullOrWhiteSpace(newName)) return;
        ViewModel.RenameGameFolder(folder.Id, newName);
    }

    private void UngroupFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as Control)?.DataContext is not GameRailFolderViewModel folder) return;
        ViewModel.UngroupGameFolder(folder.Id);
    }

    private async void RenameGameMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        // The MenuItem inherits the rail button's DataContext, which is the
        // filter row for that game.
        if ((sender as Control)?.DataContext is not FilterOptionViewModel option) return;

        var newName = await PromptRenameAsync(option.Key, "Rename game", "Game name");
        if (string.IsNullOrWhiteSpace(newName)) return;

        var (renamed, failed) = await ViewModel.RenameGameAsync(option.Key, newName);
        // Only surfaced when something actually went wrong - a clean rename
        // speaks for itself in the sidebar a moment later.
        if (failed > 0)
        {
            await ShowMessageAsync(
                "Rename incomplete",
                $"Renamed {renamed} clip(s) to \"{newName}\", but {failed} could not be moved - they're probably open in another program. " +
                "Those clips still show under the old name; renaming again once they're free will finish the job.");
        }
    }

    private async void RefreshGameIconsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshGameIconsAsync();
        // The harvest sweep is rate-limited to once every 30s and would
        // otherwise sit out the window right after a wipe - the icons of games
        // running RIGHT NOW are the cheapest ones to get back.
        _lastIconSweepUtc = DateTime.MinValue;
        HarvestGameIcons();
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
        // The hover bar is a separate top-level window sized and placed
        // against the video pane, and dragging a window edge fires this
        // continuously - trying to keep a second native window matched to a
        // rect that's changing every frame is what makes it tear, lag behind
        // and end up misplaced. Drop it for the duration instead: the poll
        // brings it straight back, correctly placed, once the drag stops and
        // the layout has settled.
        SuspendHoverControlsForResize();
        CaptureLibraryResizeAnchor();
        if (ViewModel?.IsLibraryVisible == true && _libraryResizeAnchorPath is not null)
        {
            QueueLibraryResizeAnchorRestore();
            ResetLibraryResizeAnchorSettleTimer();
        }
        else if (ViewModel?.IsLibraryVisible != true && _libraryReturnAnchorPath is not null)
        {
            // Library is collapsed right now (editor's open) - it won't see
            // its own SizeChanged/reflow until it becomes visible again, so
            // just flag that a restore is owed once that happens (see the
            // IsEditorVisible handler above) instead of trying to act now.
            _libraryReturnAnchorDirty = true;
        }

        UpdateTimelineChrome();
    }

    // TODO: Replace this with a proper layout-level anchor once the library
    // moves away from its non-virtualized WrapPanel. This deliberately hacky
    // fallback latches the first fully visible card before reflow, then makes
    // sure it is fully visible again after reflow instead of preserving an
    // unstable exact viewport fraction.
    private void CaptureLibraryResizeAnchor()
    {
        if (ViewModel?.IsLibraryVisible != true) return;

        // First SizeChanged latches the pre-reflow card. Do not replace it
        // during the same resize drag: subsequent events can already see the
        // newly wrapped layout, which is exactly what this workaround avoids.
        if (_libraryResizeAnchorPath is not null) return;

        _libraryResizeAnchorPath = ComputeLibraryAnchorPath();
    }

    // Shared by the drag-resize anchor (CaptureLibraryResizeAnchor) and the
    // editor round-trip anchor (OpenClipCardAsync) - both need "which card is
    // effectively at the top of the viewport right now", just captured at
    // different moments.
    private string? ComputeLibraryAnchorPath()
    {
        var viewportHeight = LibraryScrollViewer.Viewport.Height;
        if (viewportHeight <= 0) return null;

        var itemsControl = LibraryScrollViewer.Content as ItemsControl
            ?? LibraryScrollViewer.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
        if (itemsControl is null) return null;

        var viewportTop = LibraryScrollViewer.Offset.Y;
        var viewportBottom = viewportTop + viewportHeight;
        const double fullyVisibleTolerance = 1;
        ClipCardViewModel? firstFullyVisible = null;
        ClipCardViewModel? firstIntersecting = null;
        var firstFullyVisibleTop = double.MaxValue;
        var firstFullyVisibleLeft = double.MaxValue;
        var firstIntersectingTop = double.MaxValue;
        var firstIntersectingLeft = double.MaxValue;

        foreach (var container in itemsControl.GetRealizedContainers() ?? Enumerable.Empty<Control>())
        {
            if (container.DataContext is not ClipCardViewModel clip || !clip.IsVisibleInLibrary || !container.IsVisible || container.Bounds.Height <= 0) continue;

            var point = container.TranslatePoint(default, itemsControl);
            if (point is null) continue;

            var itemTop = point.Value.Y;
            var itemBottom = itemTop + container.Bounds.Height;
            if (itemBottom <= viewportTop || itemTop >= viewportBottom) continue;
            var itemLeft = point.Value.X;

            var isFirstInRow = itemTop < firstIntersectingTop - fullyVisibleTolerance ||
                               (Math.Abs(itemTop - firstIntersectingTop) <= fullyVisibleTolerance && itemLeft < firstIntersectingLeft);
            if (isFirstInRow)
            {
                firstIntersecting = clip;
                firstIntersectingTop = itemTop;
                firstIntersectingLeft = itemLeft;
            }

            var fullyVisible = itemTop >= viewportTop - fullyVisibleTolerance &&
                               itemBottom <= viewportBottom + fullyVisibleTolerance;
            var isFirstFullyVisible = fullyVisible &&
                                      (itemTop < firstFullyVisibleTop - fullyVisibleTolerance ||
                                       (Math.Abs(itemTop - firstFullyVisibleTop) <= fullyVisibleTolerance && itemLeft < firstFullyVisibleLeft));
            if (isFirstFullyVisible)
            {
                firstFullyVisible = clip;
                firstFullyVisibleTop = itemTop;
                firstFullyVisibleLeft = itemLeft;
            }
        }

        return (firstFullyVisible ?? firstIntersecting)?.Path;
    }

    private void LibraryScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateDateScrubberThumb();
        UpdateScrollToTopButtonVisibility();
        if (e.OffsetDelta.Y == 0) return;

        if (_libraryResizeExpectedOffsetY is double expectedOffsetY)
        {
            _libraryResizeExpectedOffsetY = null;
            if (Math.Abs(LibraryScrollViewer.Offset.Y - expectedOffsetY) <= LibraryScrollOffsetTolerance) return;
        }

        if (e.ExtentDelta.X != 0 || e.ExtentDelta.Y != 0 || e.ViewportDelta.X != 0 || e.ViewportDelta.Y != 0) return;

        // User wheel, keyboard, and date-scrubber navigation become the next
        // resize baseline. Extent/layout changes are revalidated on resize.
        ClearLibraryResizeAnchor();
    }

    private void UpdateScrollToTopButtonVisibility()
    {
        if (ViewModel is null) return;
        ViewModel.ShowScrollToTopButton = LibraryScrollViewer.Offset.Y > ScrollToTopButtonThreshold;
    }

    private void ScrollToTopButton_OnClick(object? sender, RoutedEventArgs e) => AnimateLibraryScrollToTop();

    // Eased Offset animation, stepped off TopLevel.RequestAnimationFrame
    // rather than a plain DispatcherTimer - a fixed-interval timer isn't
    // synced to the compositor's actual frame clock (and assumes 60Hz),
    // so its ticks drift against real vsync and the scroll reads as
    // jittery instead of smooth. RequestAnimationFrame's callback gets the
    // real frame timestamp, so progress tracks actual elapsed time exactly
    // regardless of refresh rate or scheduling jitter.
    private int _scrollToTopAnimationId;

    private void AnimateLibraryScrollToTop()
    {
        var startOffsetY = LibraryScrollViewer.Offset.Y;
        if (startOffsetY <= 0) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            LibraryScrollViewer.Offset = new Vector(LibraryScrollViewer.Offset.X, 0);
            return;
        }

        // Bumping this invalidates any still-running loop from a previous
        // click (e.g. clicked again mid-animation) without needing to track
        // or cancel the old callback directly.
        var animationId = ++_scrollToTopAnimationId;
        TimeSpan? startTime = null;

        void Step(TimeSpan frameTime)
        {
            if (animationId != _scrollToTopAnimationId) return;
            startTime ??= frameTime;
            var t = Math.Min(1.0, (frameTime - startTime.Value).TotalMilliseconds / ScrollToTopDuration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3);
            LibraryScrollViewer.Offset = new Vector(LibraryScrollViewer.Offset.X, startOffsetY * (1 - eased));
            if (t < 1.0) topLevel.RequestAnimationFrame(Step);
        }

        topLevel.RequestAnimationFrame(Step);
    }

    private void QueueLibraryResizeAnchorRestore()
    {
        if (_libraryResizeAnchorPath is null || _libraryResizeAnchorRestoreQueued) return;

        _libraryResizeAnchorRestoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _libraryResizeAnchorRestoreQueued = false;
            RestoreLibraryResizeAnchor(_libraryResizeAnchorPath);
        }, DispatcherPriority.Loaded);
    }

    private void ResetLibraryResizeAnchorSettleTimer()
    {
        if (_libraryResizeAnchorPath is null) return;

        _libraryResizeAnchorGeneration++;
        if (_libraryResizeAnchorSettleTimer is null)
        {
            _libraryResizeAnchorSettleTimer = new DispatcherTimer { Interval = LibraryResizeAnchorSettle };
            _libraryResizeAnchorSettleTimer.Tick += LibraryResizeAnchorSettleTimer_OnTick;
        }

        _libraryResizeAnchorSettleTimer.Stop();
        _libraryResizeAnchorSettleTimer.Start();
    }

    private void LibraryResizeAnchorSettleTimer_OnTick(object? sender, EventArgs e)
    {
        _libraryResizeAnchorSettleTimer?.Stop();
        var generation = _libraryResizeAnchorGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _libraryResizeAnchorGeneration) return;
            if (!RestoreLibraryResizeAnchor(_libraryResizeAnchorPath)) ClearLibraryResizeAnchor();
        }, DispatcherPriority.Loaded);
    }

    private bool RestoreLibraryResizeAnchor(string? anchorPath)
    {
        if (string.IsNullOrWhiteSpace(anchorPath)) return false;

        var itemsControl = LibraryScrollViewer.Content as ItemsControl
            ?? LibraryScrollViewer.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
        if (itemsControl is null || LibraryScrollViewer.Viewport.Height <= 0) return false;

        var anchorContainer = (itemsControl.GetRealizedContainers() ?? Enumerable.Empty<Control>())
            .FirstOrDefault(container => container.DataContext is ClipCardViewModel clip
                && clip.IsVisibleInLibrary
                && string.Equals(clip.Path, anchorPath, StringComparison.OrdinalIgnoreCase));
        if (anchorContainer is null) return false;

        var point = anchorContainer.TranslatePoint(default, itemsControl);
        if (point is null || anchorContainer.Bounds.Height <= 0) return false;

        var viewportTop = LibraryScrollViewer.Offset.Y;
        var viewportHeight = LibraryScrollViewer.Viewport.Height;
        var viewportBottom = viewportTop + viewportHeight;
        var itemTop = point.Value.Y;
        var itemBottom = itemTop + anchorContainer.Bounds.Height;
        const double fullyVisibleTolerance = 1;

        double targetOffset;
        if (anchorContainer.Bounds.Height >= viewportHeight)
        {
            // Oversized cards cannot fully fit. Keep their beginning visible.
            targetOffset = itemTop;
        }
        else if (itemTop < viewportTop - fullyVisibleTolerance)
        {
            targetOffset = itemTop;
        }
        else if (itemBottom > viewportBottom + fullyVisibleTolerance)
        {
            targetOffset = itemBottom - viewportHeight;
        }
        else
        {
            return true;
        }

        var maxOffset = Math.Max(0, LibraryScrollViewer.Extent.Height - LibraryScrollViewer.Viewport.Height);
        LibraryScrollViewer.Offset = new Vector(
            LibraryScrollViewer.Offset.X,
            Math.Clamp(targetOffset, 0, maxOffset));
        // ScrollChanged comes on a later layout pass. Remember actual coerced
        // offset so that delayed event does not erase this resize anchor.
        _libraryResizeExpectedOffsetY = LibraryScrollViewer.Offset.Y;

        return true;
    }

    private void ClearLibraryResizeAnchor()
    {
        _libraryResizeAnchorPath = null;
        _libraryResizeExpectedOffsetY = null;
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
    // Each distinct date, the content offset it starts at, and how many
    // visible clips fall on it - backs both the thumb's own bubble (whichever
    // date the viewport currently sits in) and the hover bubble/line
    // indicators (whichever date the CURSOR currently sits over).
    private readonly List<(string Text, double ContentY, int Count)> _scrubberDates = new();
    // One tick per date, added/removed in RebuildDateScrubber - lets the
    // track itself show where each day's clips start, not just the single
    // date the thumb or cursor currently happens to be over.
    private readonly List<Border> _scrubberTicks = new();

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

        // Same local-date key UpdateFirstOfDateFlags groups by, so the count
        // shown for a date matches what's actually on screen for it.
        var countsByDate = ViewModel.AllClips
            .Where(clip => clip.IsVisibleInLibrary)
            .GroupBy(clip => clip.CreatedAt.ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.Count());

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
            var count = countsByDate.GetValueOrDefault(localDate.Date, 1);
            _scrubberDates.Add((localDate.ToString(format).ToUpperInvariant(), offset.Value.Y, count));
        }

        // Ticks only actually exist in the visual tree while hovered (see
        // DateScrubber_OnPointerEntered/Exited) - rebuilding N Border
        // controls on every single call here otherwise meant every resize
        // frame (trackHeight changes continuously while dragging the window
        // edge, so the early "nothing changed" return above doesn't help)
        // churned the whole tick set, which was visibly laggy on anything
        // but a tiny library. Not hovered right now just means the data is
        // stale for next time; ClearScrubberTicks() below (called from
        // PointerExited) is what actually empties the canvas.
        if (_scrubberHovered) RebuildScrubberTicks();
        HighlightCurrentScrubberDate();
    }

    private bool _scrubberHovered;

    private void ClearScrubberTicks()
    {
        foreach (var tick in _scrubberTicks) DateScrubberCanvas.Children.Remove(tick);
        _scrubberTicks.Clear();
    }

    // A thin line at each date's own track position - so the track itself
    // shows where each day's clips start, not just whichever single date the
    // thumb or cursor currently happens to be over. Only built while
    // actually hovered - see the dirty-tracking comment above.
    private void RebuildScrubberTicks()
    {
        ClearScrubberTicks();

        foreach (var (_, contentY, _) in _scrubberDates)
        {
            var tick = new Border
            {
                Width = 6,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = Avalonia.Media.Brush.Parse("#8296AC"),
                IsHitTestVisible = false,
                // Starts invisible - UpdateTickProximity (called right after
                // this, and on every subsequent hover move) is what actually
                // fades each one in, only near the cursor.
                Opacity = 0,
                Transitions = new Transitions { new DoubleTransition { Property = Border.OpacityProperty, Duration = TimeSpan.FromSeconds(0.08) } }
            };
            Canvas.SetLeft(tick, 4);
            Canvas.SetTop(tick, ContentOffsetToTrackY(contentY) - 1);
            DateScrubberCanvas.Children.Add(tick);
            _scrubberTicks.Add(tick);
        }
    }

    // Only ticks near the cursor actually show, fading out with distance -
    // showing the whole timeline at once on a long library was cluttered
    // (dozens of lines competing for attention) and defeated the point of a
    // hover preview; this reads more like a magnifier following the cursor.
    private void UpdateTickProximity(double cursorTrackY)
    {
        const double fadeDistance = 90;
        for (var i = 0; i < _scrubberTicks.Count && i < _scrubberDates.Count; i++)
        {
            var tickY = ContentOffsetToTrackY(_scrubberDates[i].ContentY);
            var distance = Math.Abs(tickY - cursorTrackY);
            _scrubberTicks[i].Opacity = Math.Clamp(1 - distance / fadeDistance, 0, 1);
        }
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

        if (DateScrubberBubbleText is not null) DateScrubberBubbleText.Text = FormatScrubberBubbleText(_scrubberDates[currentIndex]);
        PositionDateScrubberBubble();
    }

    private static string FormatScrubberBubbleText((string Text, double ContentY, int Count) entry) =>
        $"{entry.Text} · {entry.Count} clip{(entry.Count == 1 ? "" : "s")}";

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

    // Hover (not dragging) variant - shows whichever date the CURSOR is over
    // rather than whichever date the viewport/thumb is on, and follows the
    // cursor's own Y instead of the thumb's.
    private void ShowHoverDateBubble(double trackY)
    {
        if (DateScrubberBubble is null || DateScrubberBubbleText is null || _scrubberDates.Count == 0)
        {
            if (DateScrubberBubble is not null) DateScrubberBubble.Opacity = 0;
            return;
        }

        var index = -1;
        for (var i = 0; i < _scrubberDates.Count; i++)
        {
            if (ContentOffsetToTrackY(_scrubberDates[i].ContentY) <= trackY + 1) index = i;
        }
        if (index < 0) index = 0;

        DateScrubberBubbleText.Text = FormatScrubberBubbleText(_scrubberDates[index]);
        DateScrubberBubble.Opacity = 1;

        var bubbleHeight = DateScrubberBubble.Bounds.Height;
        var bubbleWidth = DateScrubberBubble.Bounds.Width;
        if (bubbleHeight <= 0) bubbleHeight = 24;
        Canvas.SetTop(DateScrubberBubble, trackY - bubbleHeight / 2);
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
        var y = e.GetPosition(DateScrubberHost).Y;
        if (_draggingScrubber)
        {
            SeekLibraryToThumbTop(y - _scrubberGrabOffset);
            // Ticks are already built (PointerEntered always fires before a
            // drag can start) but this early return used to skip the one
            // call that actually keeps them tracking the cursor - only the
            // hover-not-dragging branch below updated proximity, so the
            // ticks just froze wherever they were the instant a drag began.
            UpdateTickProximity(y);
            return;
        }

        // Hovering (not dragging) previews whichever date/count is under the
        // cursor, following the cursor rather than the thumb - lets you scan
        // the whole timeline without committing to a scroll.
        ShowHoverDateBubble(y);
        UpdateTickProximity(y);
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
        _scrubberHovered = true;
        RebuildScrubberTicks();
        var y = e.GetPosition(DateScrubberHost).Y;
        SetScrubberThumbState(hovered: true, dragging: false);
        ShowHoverDateBubble(y);
        UpdateTickProximity(y);
    }

    private void DateScrubber_OnPointerExited(object? sender, PointerEventArgs e)
    {
        // Pointer capture keeps a drag alive past the track's edges, so this
        // only handles the plain hover-out case.
        if (_draggingScrubber) return;
        _scrubberHovered = false;
        ClearScrubberTicks();
        SetScrubberThumbState(hovered: false, dragging: false);
        if (DateScrubberBubble is not null) DateScrubberBubble.Opacity = 0;
    }

    private async void OpenReplaySettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectSettingsSection("Replay Buffer");
        ViewModel.OpenSettings();
        await ViewModel.RefreshOpenProcessesAsync();
    }

    // Idle (not-recording) status text. With the buffer switched off it has
    // to say so - "Replay Armed" while the master switch is off would be a
    // flat lie about whether anything is being captured.
    private string ReplayIdleStatus => ViewModel?.Settings.ReplayBufferEnabled == false ? "Replay Off" : "Replay Armed";

    // Flipping the master switch takes effect now rather than at the next
    // game-detection tick: on arms (and starts capturing straight away if a
    // game is already up), off tears the capture down.
    private async Task ApplyReplayBufferEnabledAsync()
    {
        if (ViewModel is null) return;
        if (ViewModel.Settings.ReplayBufferEnabled)
        {
            await StartReplayBufferAsync(showErrors: false);
        }
        else
        {
            await StopReplayBufferAsync();
        }

        if (!ViewModel.IsReplayRecording) ViewModel.RecorderStatus = ReplayIdleStatus;
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
            _encoderTuning.EndSession();
            ViewModel.IsReplayRecording = false;
            ViewModel.RecorderStatus = ReplayIdleStatus;
        }
        finally
        {
            _replayTransitioning = false;
        }
    }

    private async void ClipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveReplayClipAsync();
    }

    // isQualityRestart: true for the debounced restart that fires when a
    // resolution/frame rate/encoder setting changes mid-session (see
    // ReplayQualityRestartRequired's PropertyChanged handler) - the SAME game
    // session is continuing, just with a fresh buffer underneath it, so the
    // clips already saved this session must not be forgotten. Getting this
    // wrong silently emptied _sessionNewClipPaths on every such restart: any
    // clip taken before the setting change vanished from the eventual
    // "New Clips!" popup, and if none were taken after it, the popup didn't
    // appear at all despite the session having real clips in the library the
    // whole time - exactly the "finished playing, got a few clips, no popup"
    // report this was chasing down.
    private async Task StartReplayBufferAsync(bool showErrors, bool isQualityRestart = false)
    {
        if (ViewModel is null) return;
        InitializeReplayServices();
        if (_replayBuffer is null) return;
        if (_replayTransitioning) return;

        try
        {
            _replayTransitioning = true;
            if (!ViewModel.Settings.ReplayBufferEnabled || !ViewModel.ActiveGameDetection.IsDetected)
            {
                ViewModel.RecorderStatus = ReplayIdleStatus;
                return;
            }
            EnsureReplayBufferMatchesGame();
            if (_replayBuffer is null) return;
            await EnsureLibraryFolderAsync();
            ApplyPrimaryCaptureBounds();
            await Task.Run(() => _replayBuffer.StartAsync());
            AppLog.Info("Replay started.");
            _encoderTuning.BeginSession(ViewModel.Settings.ReplayEncoderPreset);
            // Fresh session, fresh list - but only for a GENUINELY new session
            // (a game was just detected). A quality restart is left open (not
            // cleared here either) so a Full Session VOD that finalizes minutes
            // after the game closed still has somewhere to land - see
            // ViewModel_OnClipAdded.
            if (!isQualityRestart)
            {
                _sessionNewClipPaths.Clear();
                // New session, so nothing has been "already seen" for it yet.
                _dismissedNewClipPaths.Clear();
            }
            _sessionCollectingClips = true;
            ViewModel.IsReplayRecording = _replayBuffer.IsRecording;
            if (ViewModel.IsReplayRecording) ShowGameDetectedNotification(ViewModel.ActiveGameDetection.DisplayName);
        }
        catch (Exception error)
        {
            AppLog.Error("Replay start failed", error);
            ViewModel.IsReplayRecording = false;
            // IsReplayRecording's setter is a no-op when the value doesn't change
            // (e.g. a second consecutive failed start while already false), which
            // would otherwise leave the status text frozen on stale "Replay Armed" -
            // set it directly so a failure always reflects in the UI.
            ViewModel.RecorderStatus = ReplayIdleStatus;
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
            ViewModel.IsSavingReplayClip = true;
            InitializeReplayServices();
            if (_replayBuffer is null || !_replayBuffer.IsRecording)
            {
                // A background auto-clip trigger firing before the buffer is actually
                // recording (e.g. CS2 launched but ClypDat hasn't caught up yet) isn't
                // worth interrupting the user over - just drop it.
                if (isAutoClip) return;
                if (ViewModel.IsReplayRecording) ViewModel.IsReplayRecording = false;
                await ShowMessageAsync("Clip failed", ViewModel.Settings.ReplayBufferEnabled
                    ? "Replay is armed, but no game is being captured yet."
                    : "The replay buffer is turned off, so there's nothing to clip. Turn it back on from the Replay Buffer menu.");
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
                // Directory creation is disk IO with no UI affinity - it has no
                // business blocking the UI thread mid-save, which is exactly
                // when the app most needs to stay responsive.
                var rootFolder = outputFolder;
                outputFolder = await Task.Run(() =>
                {
                    LibraryLayout.EnsureRoots(rootFolder);
                    return LibraryLayout.ClipsRoot(rootFolder);
                });

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
                RememberSessionClip(outputPath);
                // The save itself succeeded, but if the capture source had
                // stopped delivering frames the video is a single frozen frame -
                // say so now rather than let it be discovered on playback later.
                if (_replayBuffer.LastSaveVideoWasFrozen)
                {
                    ShowClipNotification("Clip saved - video was frozen", playSound: true);
                }
                else
                {
                    ShowClipSavedNotification();
                }
                // "3K - Mirage" -> event type "3K", map dropped - the game name
                // (not the map) is what belongs next to it as the game label.
                var autoClipEventType = autoClipLabel?.Split(" - ", 2)[0];
                var libraryFolder = ViewModel.Settings.LibraryFolder;
                var clipInfo = new ClipInfo(
                    autoClipGameName ?? ViewModel.ActiveGameDetection.DisplayName,
                    autoClipEventType,
                    autoClipLabel ?? ViewModel.ActiveGameDetection.DisplayName,
                    File.GetCreationTimeUtc(outputPath));
                // Another plain file write with no UI affinity.
                await Task.Run(() => ClipInfoSidecar.Save(libraryFolder, outputPath, clipInfo));
                await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
            }
            catch (Exception error)
            {
                AppLog.Error("Replay clip save failed", error);
                if (isAutoClip) ShowAutoClipFailedNotification();
                if (!isAutoClip) await ShowMessageAsync("Clip failed", error.Message);
            }
        }
        finally
        {
            if (ViewModel is not null) ViewModel.IsSavingReplayClip = false;
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

    // Everything the current (or most recent) session put in the library, in
    // the order it arrived, for the "New Clips!" popup shown when the game
    // closes. Paths rather than cards so a deleted clip can be dropped by
    // identity without holding a card alive.
    private readonly List<string> _sessionNewClipPaths = new();
    private bool _sessionCollectingClips;

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

    // Separate toggle from EnableClipOverlay - "clipping started" is a
    // distinct notification kind (fires once per successful buffer start,
    // see StartReplayBufferAsync) that a user may want independently of the
    // clip-saved family. No sound: this fires the instant a game launches,
    // and an audible cue for that (as opposed to a deliberate clip save) felt
    // like noise rather than useful feedback.
    // Shown when a game closes, listing what that session actually captured.
    // Re-entrant on purpose: a Full Session VOD landing later calls straight
    // back in, which rebuilds the open popup around the larger set rather than
    // stacking a second window on top of the first.
    // clipPaths defaults to the real session list; the buffer-arm test hook
    // (see StartReplayBufferAsync) passes its own preview list instead, so
    // testing the popup's layout never touches _sessionNewClipPaths or
    // interferes with the real end-of-session summary it drives.
    // Backs the fixed Delete/View All Clips buttons (NewClipsDeleteButton_OnClick
    // /NewClipsViewAllButton_OnClick below) - those are named, persistent XAML
    // elements now rather than rebuilt per show, so their Click handlers are
    // wired once in XAML and read whatever's current from here instead of each
    // getting a fresh closure-captured delegate stacked on top of the last one.
    private List<NewClipEntryViewModel> _currentNewClipsEntries = new();
    // Clips the user has already been shown and dismissed (closed the popup, or
    // clicked one to open it). Without this the popup came straight back for the
    // same clips - a late Full Session VOD landing re-shows it via
    // ViewModel_OnClipAdded - so dismissing it did not stick. Only a clip that is
    // NOT in here can bring it up again, which is what makes "don't reappear
    // until there are more clips" true. Cleared when a new session starts.
    private readonly HashSet<string> _dismissedNewClipPaths = new(StringComparer.OrdinalIgnoreCase);

    // clipPaths defaults to the real session list. The delete handler passes the
    // set that was on show instead, so rebuilding after deleting some of them
    // re-resolves exactly those - survivors stay, deleted ones stop resolving and
    // drop out - rather than whatever the session list has since become.
    private void ShowNewClipsDialog(IReadOnlyList<string>? clipPaths = null)
    {
        if (ViewModel is null || !ViewModel.Settings.ShowNewClipsOnGameClose) return;
        clipPaths ??= _sessionNewClipPaths;

        // Resolve paths to live cards each time - anything deleted (from here or
        // from the library behind it) simply stops resolving and drops out.
        var entries = clipPaths
            .Select(path => ViewModel.AllClips.FirstOrDefault(clip => string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Where(clip => clip is not null)
            .Select(clip => new NewClipEntryViewModel(clip!))
            .ToList();

        if (entries.Count == 0)
        {
            NewClipsOverlay.IsVisible = false;
            _currentNewClipsEntries = new();
            return;
        }

        // Nothing here the user hasn't already seen and dismissed - stay down.
        // A single genuinely new clip is enough to bring it back, showing the
        // whole set again for context rather than that one clip alone.
        if (entries.All(entry => _dismissedNewClipPaths.Contains(entry.Path)))
        {
            NewClipsOverlay.IsVisible = false;
            return;
        }

        _currentNewClipsEntries = entries;

        var clipCount = entries.Count(entry => !entry.IsVod);
        var vodCount = entries.Count - clipCount;
        var title = (clipCount, vodCount) switch
        {
            (0, 1) => "NEW VOD",
            (0, _) => $"{vodCount} NEW VODS",
            (1, 0) => "NEW CLIP",
            (_, 0) => $"{clipCount} NEW CLIPS",
            (1, _) => "NEW CLIP AND VOD",
            _ => $"{entries.Count} NEW CLIPS AND VODS"
        };
        NewClipsTitleText.Text = $"{title} ({FormatFileSize(entries.Sum(entry => entry.Clip.SizeBytes))})";

        // Checkboxes only earn their place when there is a choice to make.
        var multiple = entries.Count > 1;
        foreach (var entry in entries)
        {
            entry.ShowCheckBox = multiple;
            entry.SelectionChanged += (_, _) => SyncNewClipsDeleteButton();
        }
        SyncNewClipsDeleteButton();

        foreach (var entry in entries)
        {
            // The library only decodes a card's thumbnail while it is actually
            // scrolled into view, so a card that has never been on screen has a
            // null PreviewImage until asked.
            entry.Clip.SetPreviewVisible(true);
        }

        // ItemWidth gives every slot - including the last one in a row - the
        // full cardWidth + cardSpacing, so all three columns need that much
        // room; short by even one slot's spacing wraps the third card to its
        // own row with a card-sized gap of dead space beside the first two
        // (see BuildNewClipCard for the matching card.Width/Margin).
        const int cardWidth = 300;
        const int cardSpacing = 16;
        NewClipsCardsPanel.ItemWidth = cardWidth + cardSpacing;
        NewClipsCardsPanel.Children.Clear();
        foreach (var entry in entries)
        {
            var card = BuildNewClipCard(entry);
            card.Width = cardWidth;
            card.Margin = new Thickness(0, 0, cardSpacing, cardSpacing);
            NewClipsCardsPanel.Children.Add(card);
        }
        // +20 for the vertical scrollbar the ScrollViewer reserves once there
        // are enough clips to actually need one (see UpdateCardLayout for the
        // same allowance in the library grid) - dropped when this width moved
        // from the old Window to this embedded Border's own Width, which
        // reintroduced the exact "third card wraps to its own row with a
        // card-sized gap of dead space" bug fixed earlier.
        const int scrollbarAllowance = 20;
        NewClipsDialogCard.Width = 3 * (cardWidth + cardSpacing) + 44 + scrollbarAllowance;

        // Embedded in the main window now rather than its own OS Window (see
        // the XAML comment above NewClipsOverlay), so unlike the old
        // standalone-vs-owned split this needed for "ClypDat hidden to tray"
        // there is nowhere for it to render without the main window itself
        // showing - bring it forward rather than silently dropping the popup.
        if (!IsVisible) Show();
        NewClipsOverlay.IsVisible = true;
        AppLog.Info($"New Clips popup shown: {entries.Count} clip(s).");
    }

    // Which clips Delete will actually act on: whatever is ticked, or - with
    // nothing ticked - every clip on show, matching the reference's bulk
    // "Delete N clips". Kept in one place so the button's label can never
    // disagree with what pressing it does.
    private NewClipEntryViewModel[] ChosenNewClips()
    {
        var ticked = _currentNewClipsEntries.Where(entry => entry.IsSelected).ToArray();
        return ticked.Length > 0 ? ticked : _currentNewClipsEntries.ToArray();
    }

    private void SyncNewClipsDeleteButton()
    {
        var count = ChosenNewClips().Length;
        NewClipsDeleteButton.Content = count == 1 ? "Delete clip" : $"Delete {count} clips";
    }

    // Hides the popup AND records what was on show, so the same clips can't
    // bring it straight back (ViewModel_OnClipAdded and the buffer-arm hook both
    // re-show it otherwise). Only a clip that has never been dismissed will.
    private void DismissNewClipsDialog()
    {
        foreach (var entry in _currentNewClipsEntries) _dismissedNewClipPaths.Add(entry.Path);
        NewClipsOverlay.IsVisible = false;
    }

    private void NewClipsCloseButton_OnClick(object? sender, RoutedEventArgs e) => DismissNewClipsDialog();

    // The scrim spans both root rows so it darkens the title bar too, which also
    // means it swallows the title bar's own drag - leaving the whole app stuck in
    // place for as long as the popup was up. Dragging the scrim where it covers
    // the title bar moves the WINDOW instead, so the app stays movable while the
    // popup itself (being part of that window rather than its own) still cannot
    // be moved independently. Presses on the dialog card never reach here - it
    // handles its own - so only the surrounding scrim is draggable.
    private void NewClipsOverlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(NewClipsOverlay).Properties.IsLeftButtonPressed) return;
        // Row 0's height in RootLayout - matches the header the drag belongs to.
        if (e.GetPosition(NewClipsOverlay).Y > 48) return;
        BeginMoveDrag(e);
    }

    private async void NewClipsDeleteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _currentNewClipsEntries.Count == 0) return;
        foreach (var entry in ChosenNewClips())
        {
            _sessionNewClipPaths.RemoveAll(path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase));
            await ViewModel.DeleteClipAsync(entry.Clip);
        }
        // Rebuild around whatever survived - or close, when nothing did.
        ShowNewClipsDialog(_currentNewClipsEntries.Select(entry => entry.Path).ToList());
    }

    private void NewClipsViewAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        DismissNewClipsDialog();
        if (ViewModel is null) return;
        // Land on the library regardless of which view was open underneath -
        // same "close whatever's open first" approach ApplyViewHistoryEntryAsync
        // uses for its own Settings case.
        CloseEditorForNavigation();
        if (ViewModel.IsSettingsVisible) ViewModel.CloseSettings();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.##} KB";
        return $"{bytes} B";
    }

    private static string FormatTimeAgo(DateTimeOffset createdAt)
    {
        var elapsed = DateTimeOffset.UtcNow - createdAt.ToUniversalTime();
        if (elapsed < TimeSpan.FromSeconds(60)) return "A FEW SECONDS AGO";
        if (elapsed < TimeSpan.FromMinutes(2)) return "A MINUTE AGO";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} MINUTES AGO";
        if (elapsed < TimeSpan.FromHours(2)) return "AN HOUR AGO";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} HOURS AGO";
        return $"{(int)elapsed.TotalDays} DAYS AGO";
    }

    private Border BuildNewClipCard(NewClipEntryViewModel entry)
    {
        var thumbnail = new Image
        {
            Source = entry.Clip.PreviewImage,
            Stretch = Avalonia.Media.Stretch.UniformToFill,
            Height = 169
        };
        // The decode is asynchronous, so a card built before it finishes has to
        // pick the bitmap up when it lands.
        entry.Clip.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ClipCardViewModel.PreviewImage)) thumbnail.Source = entry.Clip.PreviewImage;
        };

        var duration = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#CC0B1116"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = entry.Clip.DurationLabel,
                Foreground = Avalonia.Media.Brush.Parse("#D8E2EE"),
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.Bold
            }
        };

        var check = new CheckBox
        {
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsChecked = entry.IsSelected,
            IsVisible = entry.IsCheckVisible
        };
        check.Click += (_, _) => entry.IsSelected = check.IsChecked == true;
        // Clicking the card body opens the clip (below), so the checkbox has to
        // stop its own click reaching that handler - otherwise ticking a box
        // would also fling the user into the editor.
        check.PointerPressed += (_, args) => args.Handled = true;

        var picture = new Panel
        {
            Background = Avalonia.Media.Brush.Parse("#0B1116"),
            Children = { thumbnail, duration, check }
        };

        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new PathIcon
                {
                    Data = Geometry.Parse("M12,20c-4.41,0-8-3.59-8-8s3.59-8,8-8s8,3.59,8,8S16.41,20,12,20z M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10s10-4.48,10-10S17.52,2,12,2z M12.5,7H11v6l5.25,3.15l0.75-1.23l-4.5-2.67V7z"),
                    Foreground = Avalonia.Media.Brush.Parse("#6B7C8C"),
                    Width = 12,
                    Height = 12
                },
                new TextBlock
                {
                    Text = FormatTimeAgo(entry.Clip.CreatedAt),
                    Foreground = Avalonia.Media.Brush.Parse("#6B7C8C"),
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.Bold
                }
            }
        };

        var info = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(14, 11, 14, 13),
            Children =
            {
                new TextBlock
                {
                    Text = entry.Clip.TileTopLabel.ToUpperInvariant(),
                    Foreground = Avalonia.Media.Brush.Parse("#8C98A7"),
                    FontSize = 12,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = entry.Clip.TileMainLabel,
                    Foreground = Avalonia.Media.Brush.Parse("#D8E4F2"),
                    FontSize = 14,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                },
                timeRow
            }
        };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(picture, 0);
        Grid.SetRow(info, 1);
        layout.Children.Add(picture);
        layout.Children.Add(info);

        var card = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#161D25"),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = Avalonia.Media.Brush.Parse("#232F3A"),
            Child = layout
        };

        void SyncCardState()
        {
            check.IsChecked = entry.IsSelected;
            check.IsVisible = entry.IsCheckVisible;
            card.BorderBrush = entry.IsSelected
                ? Avalonia.Media.Brush.Parse("#5864E8")
                : entry.IsHovered ? Avalonia.Media.Brush.Parse("#3A4856") : Avalonia.Media.Brush.Parse("#232F3A");
        }

        entry.PropertyChanged += (_, _) => SyncCardState();
        card.Cursor = new Cursor(StandardCursorType.Hand);
        card.PointerEntered += (_, _) => entry.IsHovered = true;
        card.PointerExited += (_, _) => entry.IsHovered = false;
        // Card body opens the clip; the checkbox above marks it for the Delete
        // button instead, and swallows its own click so the two don't collide.
        card.PointerPressed += async (_, _) =>
        {
            // Dismiss before opening: the editor is what the user asked for, and
            // leaving the popup up over it (or letting it come back) would bury
            // the very clip they just chose.
            DismissNewClipsDialog();
            await OpenClipCardAsync(entry.Clip);
        };

        return card;
    }

    private void RememberSessionClip(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_sessionNewClipPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))) return;
        _sessionNewClipPaths.Add(path);
    }

    // A Full Session VOD is muxed on a background thread and can land minutes
    // after the game closed, so it is never available at the moment the popup
    // would first be shown. Collecting it here instead means it either joins a
    // popup that is still open, or brings up its own - both correct, and
    // neither makes the clips wait on the mux.
    private void ViewModel_OnClipAdded(object? sender, ClipCardViewModel clip)
    {
        if (!_sessionCollectingClips || ViewModel is null) return;
        if (!clip.IsVod) return;
        RememberSessionClip(clip.Path);
        if (NewClipsOverlay.IsVisible) ShowNewClipsDialog();
        else if (!ViewModel.IsReplayRecording) ShowNewClipsDialog();
    }

    private void ShowGameDetectedNotification(string gameName)
    {
        if (ViewModel is null || !ViewModel.Settings.EnableGameDetectedOverlay) return;
        try
        {
            // The one notification with something to teach: it fires as the
            // buffer arms, which is exactly the moment the save hotkey becomes
            // worth knowing. Every other overlay in this family reports
            // something that already happened and stays a single line.
            ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition, $"Clipping started - {gameName}", playSound: false,
                hotkey: ViewModel.Settings.SaveReplayHotkey, hotkeyHint: "to save a clip");
        }
        catch (Exception error)
        {
            AppLog.Error("Game detected overlay failed", error);
        }
    }

    // "Auto clip started - X detected, ..." - a GSI listener spotting a
    // highlight-worthy event, separate from the clip-saved family below
    // (EnableClipOverlay covers the actual save lifecycle).
    private void ShowAutoClipPendingNotification(string message)
    {
        if (ViewModel is null || !ViewModel.Settings.EnableAutoClipPendingOverlay) return;
        try
        {
            ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition, message, playSound: false);
        }
        catch (Exception error)
        {
            AppLog.Error("Auto clip pending overlay failed", error);
        }
    }

    private void ShowAutoClipFailedNotification()
    {
        if (ViewModel is null || !ViewModel.Settings.EnableAutoClipFailedOverlay) return;
        try
        {
            ShowClipSavedOverlay(ViewModel.Settings.ClipOverlayPosition, "Auto clip failed", playSound: false);
        }
        catch (Exception error)
        {
            AppLog.Error("Auto clip failed overlay failed", error);
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

    private Border? _overlayBadge;
    private Border? _overlayAccent;
    private TextBlock? _overlayLabel;
    private StackPanel? _overlayHintRow;
    private TranslateTransform? _overlayTranslate;
    private DispatcherTimer? _overlayHideTimer;

    // Built once and reused for the process lifetime. Each notification used to
    // construct a brand-new transparent, topmost Window (and destroy the
    // previous one), so a single clip save churned two or three compositor
    // surfaces in about two seconds - which is real GPU/DWM work landing at
    // exactly the moment the machine is busiest, and the most likely reason a
    // save made the mouse feel choppy. Show/Hide costs none of that.
    private void EnsureClipOverlay()
    {
        if (_activeClipOverlay is not null) return;

        // A full-height accent stripe (not a small dot) plus a solid, near-
        // opaque background - meant to actually stand out at a glance over
        // gameplay, not blend in as a subtle little pill. "AccentBrush" is
        // the same live, OS-accent-colour-tracking resource App.axaml.cs
        // keeps in sync with Windows' own accent colour (see
        // InitializeAccentColor/ApplyAccentColor) - using it here instead of
        // a fixed hex means the overlay follows the system colour too,
        // rather than always showing ClypDat's old fixed teal regardless of
        // what the user picked in Windows.
        var accentBrush = (Application.Current?.Resources["AccentBrush"] as IBrush) ?? Avalonia.Media.Brush.Parse("#13C8B5");
        _overlayAccent = new Border
        {
            Width = 5,
            Background = accentBrush,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _overlayLabel = new TextBlock
        {
            Foreground = Avalonia.Media.Brush.Parse("#F5F9FF"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            // An auto-clip's label (e.g. a long kill-streak/event name) has no
            // length cap of its own, and this badge sizes itself to whatever
            // the label measures at - unwrapped, a long one stretched the
            // badge most of the way across the screen instead of staying the
            // same compact size every other notification is. Wrapping plus a
            // width cap keeps it bounded like the rest of them.
            MaxWidth = 340,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        // Second line, only populated when a message has a hotkey to teach -
        // collapsed otherwise so every other notification stays the single
        // centred line it has always been rather than growing a blank row.
        _overlayHintRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        var textColumn = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _overlayLabel, _overlayHintRow }
        };

        var icon = new Image
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-48.png")))
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(18, 16, 24, 16),
            Children = { icon, textColumn }
        };
        _overlayTranslate = new TranslateTransform();
        _overlayBadge = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#F5141D24"),
            BorderBrush = Avalonia.Media.Brush.Parse("#3C4C5A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 10 28 0 #70000000"),
            RenderTransform = _overlayTranslate,
            ClipToBounds = true,
            Child = new DockPanel { Children = { _overlayAccent, content } }
        };

        _activeClipOverlay = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            // Height only - Width is assigned per show, since it has to reach
            // from the badge's resting position out to the screen edge to give
            // the slide somewhere to go. See ShowClipSavedOverlay.
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _overlayBadge
        };
        _activeClipOverlay.Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(_activeClipOverlay, _overlayBadge.Background, b => _overlayBadge.Background = b);

        // Movement only, deliberately no opacity transition: the badge slides
        // in and out from off screen and never fades. A cross-fade on top of
        // the slide is what made the old 28px nudge read as "appears and
        // disappears" rather than as something arriving from the edge.
        //
        // Attached once. Only ever driven by assigning X below, and the enter
        // state is set without the transition in effect by assigning before the
        // window is shown.
        _overlayTranslate.Transitions =
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(260),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut()
            }
        ];

        // Warm-up realize: show and immediately hide, fully off-screen, so this
        // window/control tree is attached to a real compositor context (actual
        // font metrics, actual DPI scaling) before it is ever measured for real.
        // ShowActivated=false above means this steals no focus. Without it, the
        // very FIRST "Saving clip..." of a session measured _overlayBadge
        // against a NEVER-SHOWN window - Avalonia's Measure falls back to
        // different (observed: wider) text metrics before a control has ever
        // been attached to a TopLevel - so only that first toast came out
        // visibly elongated; every one after it measured correctly because by
        // then the window had already been shown once. This makes that "once"
        // happen here, invisibly, instead of live in front of the user.
        _activeClipOverlay.Position = new PixelPoint(-32000, -32000);
        _activeClipOverlay.Show();
        _activeClipOverlay.Hide();
    }

    // The monitor the GAME is on, not the one the main window happens to be on.
    // While playing, the main window is usually minimised to the tray or parked
    // on a second display, and ScreenFromWindow(this) then puts the overlay on a
    // monitor the player isn't looking at - indistinguishable, from the chair,
    // from the overlay never appearing at all.
    private Avalonia.Platform.Screen? ScreenForOverlay()
    {
        var gameHandle = ViewModel?.ActiveGameDetection.WindowHandle ?? IntPtr.Zero;
        if (gameHandle != IntPtr.Zero && IsWindowVisible(gameHandle) && GetWindowRect(gameHandle, out var gameRect))
        {
            var centre = new PixelPoint(
                gameRect.Left + (gameRect.Right - gameRect.Left) / 2,
                gameRect.Top + (gameRect.Bottom - gameRect.Top) / 2);
            var gameScreen = Screens.ScreenFromPoint(centre);
            if (gameScreen is not null) return gameScreen;
        }

        return Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
    }

    // "Ctrl+Shift+F9" -> [Ctrl] + [Shift] + [F9] as keycap chips, followed by
    // whatever the hint says the keys do. Built from the live setting rather
    // than hardcoded, so a rebound hotkey teaches the right keys.
    private void BuildOverlayHint(string hotkey, string trailingText)
    {
        if (_overlayHintRow is null) return;
        _overlayHintRow.Children.Clear();

        var keys = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                _overlayHintRow.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = Avalonia.Media.Brush.Parse("#8DA0B4"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            _overlayHintRow.Children.Add(new Border
            {
                Background = Avalonia.Media.Brush.Parse("#2A323C"),
                BorderBrush = Avalonia.Media.Brush.Parse("#49525E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = keys[i],
                    Foreground = Avalonia.Media.Brush.Parse("#E8EEF6"),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 13
                }
            });
        }

        _overlayHintRow.Children.Add(new TextBlock
        {
            Text = trailingText,
            Foreground = Avalonia.Media.Brush.Parse("#A8B8C8"),
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private void ShowClipSavedOverlay(string position, string text, bool playSound, string? hotkey = null, string hotkeyHint = "")
    {
        _activeClipOverlayCloseTimer?.Stop();
        _activeClipOverlayCloseTimer = null;
        _overlayHideTimer?.Stop();
        _overlayHideTimer = null;

        EnsureClipOverlay();
        if (_activeClipOverlay is null || _overlayBadge is null || _overlayLabel is null || _overlayAccent is null || _overlayTranslate is null) return;

        // Position is a live setting, so the side has to be re-applied on every
        // show rather than baked in at construction.
        var isLeft = string.Equals(position, "Top Left", StringComparison.OrdinalIgnoreCase);
        _overlayLabel.Text = text;

        var showHint = !string.IsNullOrWhiteSpace(hotkey);
        if (showHint) BuildOverlayHint(hotkey!, hotkeyHint);
        if (_overlayHintRow is not null) _overlayHintRow.IsVisible = showHint;

        // Square on the side touching the screen edge, rounded on the side
        // facing in - a fully rounded badge sitting flush reads as a gap, since
        // the curve pulls the fill away from the edge at the corners. The
        // accent stripe stays square and is shaped by the badge's ClipToBounds.
        _overlayBadge.CornerRadius = isLeft ? new CornerRadius(0, 8, 8, 0) : new CornerRadius(8, 0, 0, 8);
        _overlayAccent.CornerRadius = new CornerRadius(0);
        DockPanel.SetDock(_overlayAccent, isLeft ? Dock.Left : Dock.Right);

        _overlayBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredWidth = _overlayBadge.DesiredSize.Width;

        var screen = ScreenForOverlay();
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;
        // Flush to the side it is pinned to - no horizontal gap. Vertically it
        // still sits down from the top edge so it clears a game's own HUD.
        const double TopMarginDips = 24;

        // Window is exactly the badge's width and sits against the screen edge,
        // so translating the badge by that full width carries it past the
        // window's own bounds, where it is clipped - which is what actually
        // reads as sliding off the screen. A window any wider than the badge
        // would leave the very gap this is meant not to have.
        var travel = desiredWidth;
        _activeClipOverlay.Width = travel;

        var travelDevicePixels = (int)Math.Round(travel * scaling);
        var topMarginDevicePixels = (int)Math.Round(TopMarginDips * scaling);
        var x = isLeft ? area.X : area.X + area.Width - travelDevicePixels;
        _activeClipOverlay.Position = new PixelPoint(x, area.Y + topMarginDevicePixels);

        _overlayBadge.HorizontalAlignment = HorizontalAlignment.Stretch;

        // Slides in FROM the edge it's pinned to, toward its resting position -
        // left-pinned slides in moving right, right-pinned slides in moving left
        // (the "reverse"). Flipped to the resting value one frame later so the
        // transition has a "from" state to animate away from, instead of both
        // values landing in the same layout pass with nothing in between.
        //
        // Transitions detached while the start state is assigned. X sits at 0
        // the very first time this runs, so with them attached the assignment
        // below was itself animated: the badge slid OUT to the start position
        // in full view before sliding back in. Only visible on the session's
        // first overlay, because every later one already ends at ±travel and
        // re-assigning it is a no-op.
        var transitions = _overlayTranslate.Transitions;
        _overlayTranslate.Transitions = null;
        _overlayTranslate.X = isLeft ? -travel : travel;

        _activeClipOverlay.Show();

        // Topmost at construction only puts the window in the topmost band once.
        // A game that goes fullscreen afterwards enters that same band and sits
        // ABOVE it, and Show() on an already-created window doesn't re-assert
        // anything - so the second and every later overlay of a session came up
        // behind the game. Push it back to the front of the band on every show.
        //
        // Without activating, and NOACTIVATE on top of that: moving focus onto
        // a window over a fullscreen game is what makes the game minimise, and
        // an overlay that costs you the game is worse than no overlay.
        MakeWindowNonActivating(_activeClipOverlay);
        MakeWindowClickThrough(_activeClipOverlay);
        ApplyCaptureExclusion(_activeClipOverlay, ViewModel?.Settings.ExcludeOverlaysFromCapture ?? true);
        var overlayHandle = NativeHandleOf(_activeClipOverlay);
        if (overlayHandle != IntPtr.Zero)
        {
            SetWindowPos(overlayHandle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayTranslate is null) return;
            _overlayTranslate.Transitions = transitions;
            _overlayTranslate.X = 0;
            // Sound used to fire the instant this method was called - well
            // before the slide transition below even started, so it landed a
            // couple hundred ms ahead of anything visibly happening. Playing it
            // here instead, right as the slide-in begins, actually lines the
            // two up.
            if (playSound) PlayClipNotificationSound();
        }, DispatcherPriority.Loaded);

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            _activeClipOverlayCloseTimer = null;
            // Slide back out the same way it came in - no fade, it just leaves -
            // then hide once that transition has had time to finish playing.
            if (_overlayTranslate is not null) _overlayTranslate.X = isLeft ? -travel : travel;
            var hideAfterExit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            hideAfterExit.Tick += (_, _) =>
            {
                hideAfterExit.Stop();
                _overlayHideTimer = null;
                _activeClipOverlay?.Hide();
            };
            _overlayHideTimer = hideAfterExit;
            hideAfterExit.Start();
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
                ViewModel.RecorderStatus = ReplayIdleStatus;
            }
        });
    }

    private async Task RefreshRemoteGameIconsAsync()
    {
        // Curated icon overrides ride the same once-a-day cadence. Only used
        // for games the Steam store search resolves wrongly or not at all, so
        // a failure here costs nothing.
        await RemoteGameIconsService.RefreshAsync();
    }

    private async Task RefreshRemoteGameCatalogAsync()
    {
        var updated = await RemoteGameCatalogService.RefreshAsync();
        if (updated is not null) _gameDetector.ApplyRemoteCatalog(updated);
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
        _clipHoverPreview.Stop("clip opened");
        // Snapshot while Library is still visible/laid out - once
        // OpenClipAsync flips IsEditorVisible, LibraryScrollViewer collapses
        // and its containers stop reflecting real layout until it's shown
        // again. See the IsEditorVisible PropertyChanged handler for the
        // restore half of this.
        if (ViewModel.IsLibraryVisible)
        {
            _libraryReturnAnchorPath = ComputeLibraryAnchorPath();
            _libraryReturnAnchorDirty = false;
        }
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

    private async void ClipContextShare_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;
        if (!ViewModel.PrepareClipForShare(clip)) return;
        await ShareCurrentClipAsync();
    }

    private async void ClipCardShare_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipCardViewModel clip } || ViewModel is null) return;
        if (!ViewModel.PrepareClipForShare(clip)) return;
        await ShareCurrentClipAsync();
    }

    private async void ClipContextRename_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        // Renaming from inside a multi-selection applies to all of it; a
        // right-click on a card that isn't part of the selection is still a
        // plain single rename of that card.
        var selected = ViewModel.AllClips.Where(item => item.IsSelected).ToArray();
        if (clip.IsSelected && selected.Length > 1)
        {
            await RenameClipsAsync(selected);
            return;
        }

        await RenameClipCardAsync(clip);
    }

    // Medal imports whose game came through wrong or unparseable - they land
    // in "Unknown Game" together despite being from different games, so this
    // is per clip rather than a rename of the whole group.
    //
    // A nested MenuItem submenu (Items populated dynamically from the
    // ContextMenu's own Opening event, same pattern GameContextMenu_OnOpening
    // already uses for "Move to folder") never actually showed its dropdown
    // here. MenuFlyout next - fixed showing it up (see git history for the
    // detached-anchor cause), but its Menu-based dismiss semantics never
    // reliably light-dismissed on an outside click when built ad hoc like
    // this. Plain Flyout is a simpler primitive built specifically for "show
    // a popover, close it on an outside click", with its Content just a
    // StackPanel of ordinary Buttons styled to look like menu rows.
    //
    // StaysOpenOnClick on the XAML MenuItem keeps the parent ContextMenu
    // open (rather than the whole menu vanishing the instant "Change game"
    // is interacted with, only for this flyout to then appear in its place)
    // - the MenuItem itself is now guaranteed to stay attached to the tree
    // too, so it doubles as a real anchor: the flyout opens beside it, to
    // the right, like a genuine submenu would. Opens on hover
    // (PointerEntered), same as a real submenu, rather than requiring a
    // click - the row's own header carries a right-pointing arrow (in XAML)
    // to signal that.
    private Flyout? _changeGameFlyout;
    private MenuItem? _changeGameMenuItem;
    private Control? _changeGameFlyoutContent;
    private DispatcherTimer? _changeGameFlyoutHoverTimer;

    // Since Change Game opens on hover rather than a click, hovering any
    // OTHER row in the same context menu needs to close it too - otherwise
    // it just sits there covering/blocking the rest of the menu. This alone
    // turned out not to be enough: an open Popup captures the pointer for
    // its own light-dismiss tracking, which means the sibling rows stop
    // receiving PointerEntered at all while the flyout is open, not just
    // while the cursor happens to be over it - "hovering another row" never
    // fires in the first place. Kept as a harmless backup for whichever
    // path (if any) still gets through.
    private void ClipContextMenuItem_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _changeGameFlyout?.Hide();
    }

    // A Flyout lives on a separate popup surface and captures pointer input,
    // so neither the trigger row nor sibling menu rows reliably receive
    // leave/enter events while it is open. Poll the screen cursor while this
    // one flyout is visible instead; both controls can always report their
    // screen bounds regardless of the popup's input routing.
    private void StartChangeGameFlyoutHoverTracking()
    {
        _changeGameFlyoutHoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _changeGameFlyoutHoverTimer.Tick -= ChangeGameFlyoutHoverTimer_OnTick;
        _changeGameFlyoutHoverTimer.Tick += ChangeGameFlyoutHoverTimer_OnTick;
        _changeGameFlyoutHoverTimer.Start();
    }

    private void StopChangeGameFlyoutHoverTracking()
    {
        _changeGameFlyoutHoverTimer?.Stop();
    }

    private void ChangeGameFlyoutHoverTimer_OnTick(object? sender, EventArgs e)
    {
        if (_changeGameFlyout is null)
        {
            StopChangeGameFlyoutHoverTracking();
            return;
        }

        if (!GetCursorPos(out var cursor)) return;
        if (!IsCursorOverChangeGameFlyout(cursor)) _changeGameFlyout.Hide();
    }

    private bool IsCursorOverChangeGameFlyout(CursorPoint cursor)
    {
        if (_changeGameMenuItem is null || _changeGameFlyoutContent is null) return false;
        if (!TryGetScreenBounds(_changeGameMenuItem, out var rowTopLeft, out var rowBottomRight)) return false;

        if (IsCursorWithin(cursor, rowTopLeft, rowBottomRight)) return true;
        if (!TryGetScreenBounds(_changeGameFlyoutContent, out var flyoutTopLeft, out var flyoutBottomRight)) return false;

        // Includes the presenter's border/padding around the ScrollViewer.
        const int flyoutPadding = 8;
        if (IsCursorWithin(cursor, flyoutTopLeft, flyoutBottomRight, flyoutPadding)) return true;

        // Keep a narrow bridge across the intentional placement offset, so
        // crossing from the parent row into the submenu cannot dismiss it.
        var bridgeLeft = Math.Min(rowBottomRight.X, flyoutTopLeft.X - flyoutPadding);
        var bridgeRight = Math.Max(rowBottomRight.X, flyoutTopLeft.X - flyoutPadding);
        var bridgeTop = Math.Max(rowTopLeft.Y, flyoutTopLeft.Y - flyoutPadding);
        var bridgeBottom = Math.Min(rowBottomRight.Y, flyoutBottomRight.Y + flyoutPadding);
        return cursor.X >= bridgeLeft && cursor.X < bridgeRight
               && cursor.Y >= bridgeTop && cursor.Y < bridgeBottom;
    }

    private static bool TryGetScreenBounds(Control control, out PixelPoint topLeft, out PixelPoint bottomRight)
    {
        topLeft = default;
        bottomRight = default;
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;

        try
        {
            topLeft = control.PointToScreen(new Point(0, 0));
            bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
            return bottomRight.X > topLeft.X && bottomRight.Y > topLeft.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsCursorWithin(CursorPoint cursor, PixelPoint topLeft, PixelPoint bottomRight, int padding = 0)
    {
        return cursor.X >= topLeft.X - padding && cursor.X < bottomRight.X + padding
               && cursor.Y >= topLeft.Y - padding && cursor.Y < bottomRight.Y + padding;
    }

    private void ClipContextSetGame_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not MenuItem { DataContext: ClipCardViewModel clip } menuItem) return;

        _changeGameFlyout?.Hide();
        _changeGameMenuItem = menuItem;

        var flyout = new Flyout
        {
            Placement = Avalonia.Controls.PlacementMode.RightEdgeAlignedTop,
            HorizontalOffset = 8
        };
        _changeGameFlyout = flyout;
        if (!menuItem.Classes.Contains("changeGameMenuItemOpen")) menuItem.Classes.Add("changeGameMenuItemOpen");
        flyout.Closed += (_, _) =>
        {
            menuItem.Classes.Remove("changeGameMenuItemOpen");
            if (_changeGameFlyout == flyout)
            {
                _changeGameFlyout = null;
                _changeGameMenuItem = null;
                _changeGameFlyoutContent = null;
                StopChangeGameFlyoutHoverTracking();
            }
        };

        void PickGame(string? gameName)
        {
            flyout.Hide();
            _ = ChangeClipGameAsync(clip, gameName);
        }

        var list = new StackPanel { Spacing = 2, MinWidth = 160 };

        // Null gameName tells ChangeClipGameAsync to prompt for a brand new
        // name instead of using one already in the library.
        var addGame = new Button { Classes = { "flyoutMenuItem" }, Content = "+ Add Game" };
        addGame.Click += (_, _) => PickGame(null);
        list.Children.Add(addGame);

        var otherGames = ViewModel.GameFilterOptions
            .Select(option => option.Key)
            .Where(key => !string.Equals(key, clip.GameFilterKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (otherGames.Length > 0) list.Children.Add(new Separator());
        foreach (var game in otherGames)
        {
            var item = new Button { Classes = { "flyoutMenuItem" }, Content = game };
            item.Click += (_, _) => PickGame(game);
            list.Children.Add(item);
        }

        var flyoutContent = new ScrollViewer { MaxHeight = 320, Content = list };
        _changeGameFlyoutContent = flyoutContent;
        flyout.Content = flyoutContent;

        flyout.ShowAt(menuItem);
        StartChangeGameFlyoutHoverTracking();
    }

    private async Task ChangeClipGameAsync(ClipCardViewModel clip, string? gameName)
    {
        if (ViewModel is null) return;

        var selected = ViewModel.AllClips.Where(item => item.IsSelected).ToArray();
        var targets = clip.IsSelected && selected.Length > 1 ? selected : new[] { clip };

        if (gameName is null)
        {
            var heading = targets.Length > 1 ? $"Change game for {targets.Length} clips" : "Change game";
            // Prefilled with what it's filed under now, so correcting a
            // spelling doesn't mean retyping the whole name.
            gameName = await PromptRenameAsync(clip.GameFilterKey, heading, "Game name");
            if (string.IsNullOrWhiteSpace(gameName)) return;
        }

        var (moved, failed) = await ViewModel.SetClipsGameAsync(targets, gameName);
        if (failed > 0)
        {
            await ShowMessageAsync(
                "Some clips couldn't be moved",
                $"Filed {moved} clip(s) under \"{gameName.Trim()}\", but {failed} could not be moved - they're probably open in another program.");
        }
    }

    private async Task RenameClipsAsync(IReadOnlyList<ClipCardViewModel> clips)
    {
        if (ViewModel is null || clips.Count == 0) return;

        var newTitle = await PromptRenameAsync(string.Empty, $"Rename {clips.Count} clips", "Clip title");
        if (newTitle is null) return;
        var trimmed = newTitle.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        // Every clip gets the same title; the filename builder appends each
        // clip's own timestamp (and de-duplicates beyond that), so they don't
        // collide on disk.
        foreach (var clip in clips)
        {
            await ApplyClipTitleRenameAsync(clip, trimmed);
        }
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
            _clipHoverPreview.Stop("clip deleted");
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
        _clipHoverPreview.Request(clip, ViewModel?.EnableClipHoverPreview == true && ViewModel.IsLibraryVisible);
    }

    private void ClipCard_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ClipCardViewModel clip }) return;
        clip.IsHovered = false;
        _clipHoverPreview.StopIfActive(clip, "pointer exited");
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
        var visible = viewport.Width > 0 && viewport.Height > 0;
        if (!visible) _clipHoverPreview.StopIfActive(clip, "card left viewport");
        clip.SetPreviewVisible(visible);
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
            _clipHoverPreview.Stop("selected clips deleted");
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
        var dialog = CreateDialog("Rename all clips?", "This renames every video in the current library to the selected filename scheme. Existing files are never overwritten.", true, "Rename", destructive: false);
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

        if (change.Property == WindowStateProperty && change.GetOldValue<WindowState>() == WindowState.Minimized && WindowState != WindowState.Minimized)
        {
            // Restoring from minimized (e.g. alt-tabbing back out of an
            // exclusive-fullscreen game that forced this window down) is the
            // same owned-window desync as Activated/Deactivated above - reset
            // both so the next poll tick re-shows them correctly instead of
            // trusting IsVisible state the OS invalidated while minimized.
            HideEditorHoverControls(immediate: true);
            _recordingPausedOverlay?.Hide();
        }

        if (change.Property == WindowStateProperty && MaximizeRestoreButton?.Content is PathIcon icon)
        {
            var isMaximized = WindowState == WindowState.Maximized;
            // Restore used to be two full rounded squares, which reads as a
            // copy/duplicate glyph rather than "shrink this window back".
            // Windows' own restore glyph is a single square with only the
            // top and right EDGES of a second one peeking out behind it -
            // that's the L-shape here (F0 = even-odd, so the front square
            // renders as an outline instead of a filled block).
            icon.Data = Geometry.Parse(isMaximized
                ? "F0 M5,8H16V19H5V8z M7,10V17H14V10H7z M8,5H19V16H17V7H8V5z"
                : "F0 M4,4H20V20H4V4z M6,6V18H18V6H6z");
            ToolTip.SetTip(MaximizeRestoreButton, isMaximized ? "Restore" : "Maximize");
        }
    }

    // Back/Forward header nav across all three top-level views. The Editor
    // needs its clip path recorded alongside the view kind, since re-entering
    // it means reopening one specific clip rather than just flipping a flag -
    // without that it couldn't be in the history at all, which is why Back
    // sat permanently disabled while a clip was open.
    private enum ViewHistoryKind { Library, Settings, Editor }

    // GameKey/ClipTypeKey only mean anything for a Library entry - the rail
    // selection active at that point, so Back/Forward step through the
    // sequence of game/section filters visited, not just editor and settings
    // (which is all this used to track).
    private readonly record struct ViewHistoryEntry(ViewHistoryKind Kind, string? ClipPath, string? GameKey = null, string? ClipTypeKey = null);

    private readonly List<ViewHistoryEntry> _viewHistory = new() { new ViewHistoryEntry(ViewHistoryKind.Library, null) };
    private int _viewHistoryIndex;
    private bool _navigatingViewHistory;

    private ViewHistoryEntry CurrentViewState()
    {
        if (ViewModel is null) return new ViewHistoryEntry(ViewHistoryKind.Library, null);
        if (ViewModel.IsEditorVisible) return new ViewHistoryEntry(ViewHistoryKind.Editor, ViewModel.SelectedVideoPath);
        if (ViewModel.IsSettingsVisible) return new ViewHistoryEntry(ViewHistoryKind.Settings, null);
        return new ViewHistoryEntry(ViewHistoryKind.Library, null, ViewModel.ActiveGameFilterKey, ViewModel.ActiveClipTypeFilterKey);
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
        StopEditorPlayback(stopMode: PlaybackStopMode.Background);
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
                    // Game first, then clip-type - SelectClipTypeSection's own
                    // non-Combine clearing of the game side runs after the
                    // game is already set, so it only fires (harmlessly) when
                    // there really is no game to restore. Order matters; the
                    // other way round would let it wipe out the game restore.
                    ViewModel.SelectGameSection(entry.GameKey);
                    ViewModel.SelectClipTypeSection(entry.ClipTypeKey);
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
        // Same reparent hazard as the hover bar above - hide the badge before
        // the Remove/Add below instead of leaving it to reposition itself
        // against a momentarily-detached EditorVideoView. It's re-evaluated
        // (and re-shown if still applicable) by the next timer tick or layout
        // event once the view has settled into FullscreenVideoHost.
        _recordingPausedOverlay?.Hide();

        // Move the SAME EditorVideoView (already playing) into the
        // fullscreen host instead of hot-swapping MediaPlayer onto a second
        // VideoView - that never actually rendered a frame into the new
        // surface (tried twice, confirmed via logs the swap ran but stayed
        // black). The control's MediaPlayer is never touched here.
        EditorVideoHost.Children.Remove(EditorVideoView);
        FullscreenVideoHost.Children.Add(EditorVideoView);
        Dispatcher.UIThread.Post(EditorVideoView.RefreshClickHook, DispatcherPriority.Loaded);
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
        Dispatcher.UIThread.Post(EditorVideoView.RefreshClickHook, DispatcherPriority.Loaded);
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
        StopEditorPlayback(stopMode: PlaybackStopMode.Background);
        ViewModel?.CloseEditor();
    }

    // The ClypDat logo button is a universal "go back to Library" from anywhere
    // else in the app (editor or Settings). Opening Settings has its own
    // dedicated button now (bottom-left of the Library). It's also the way
    // back to the unfiltered library - "home" that left a game filter applied
    // wasn't home, and with multi-select on there could be several to undo.
    private void LibraryHomeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        ViewModel.ClearAllFilters();

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


    private void ClearSettingsSearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.SettingsSearchText = string.Empty;
    }

    private void ClearLibrarySearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.LibrarySearchText = string.Empty;
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

    private void ExportCaptureDiagnosticsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = CaptureDiagnosticBundle.Create(_replayBuffer);
            ExplorerService.Open(path, selectFile: true);
        }
        catch (Exception error)
        {
            AppLog.Error("Capture diagnostic bundle export failed", error);
        }
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
        Dispatcher.UIThread.Post(() => ShowAutoClipPendingNotification(message));
    }

    private void Cs2GsiListener_OnAutoClipReady(object? sender, Cs2AutoClipRequest request)
    {
        AutoClip_OnReady(sender, new AutoClipRequest("cs2", "Counter-Strike 2", request.Title, request.Title, request.StartUtc, request.EndUtc));
    }

    private void AutoClip_OnPending(object? sender, string message) => Dispatcher.UIThread.Post(() => ShowAutoClipPendingNotification(message));

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
                StopEditorPlayback(stopMode: PlaybackStopMode.Background);
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
            PauseEditorPlayback();
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
        if (_playback.IsSeeking)
        {
            // A seek owns VLC transport until it settles. Replace its paused
            // request with a serialized resume seek rather than letting
            // PlayFrom race it with a separate Stop/Play sequence.
            _ = ApplyTimelineSeekAsync(startTime, resumePlayback: true);
            return;
        }
        _playback.PlayFrom(startTime);
        StartPlayheadClock(startTime);
        ViewModel.IsPlaying = true;
        _playbackTimer.Start();
    }

    private void PauseEditorPlayback()
    {
        if (ViewModel is null || !ViewModel.IsPlaying || _playback is null) return;
        _playback.Pause();
        var pauseTime = _playback.Position;
        ViewModel.CurrentTime = pauseTime;
        SetPlayheadBase(pauseTime);
        ViewModel.IsPlaying = false;
        _playbackTimer.Stop();
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
        // Scrubbing the surface genuinely does mean "go where I clicked".
        _trimDragGrabOffsetMs = 0;
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
        _trimDragGrabOffsetMs = TrimGrabOffsetMs(e, ViewModel.TrimStart);
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
        _trimDragGrabOffsetMs = TrimGrabOffsetMs(e, ViewModel.TrimEnd);
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
            if (mode == TimelineDragMode.TrimStart) ViewModel.RegenerateThumbnailAtTrimStart();
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

            // The ".paused.json" sidecar records pause ranges as offsets into
            // the ORIGINAL recording. A trim just replaced that file with a
            // shorter one starting at a different point on that original
            // timeline, so every stored offset now points at the wrong moment
            // (or a moment that got trimmed away entirely) - the "Playback
            // Paused" badge showing over content that plainly isn't paused is
            // exactly that stale offset landing in the new, shorter file.
            // Deriving the correct shifted offsets would need to know the
            // pre-trim TrimStart at export time, and this only runs after the
            // file has already been replaced - simplest correct fix is to drop
            // the sidecar: a trimmed clip is user-authored content the badge
            // was never meant to second-guess anyway.
            // ALL THREE locations LoadPausedRanges falls back through, not just
            // the current one - a clip whose sidecar lives at either legacy path
            // (the "Clip Info" subfolder, or plain adjacent to the video) kept
            // its stale ranges and went on showing the badge after a trim,
            // because deleting only the primary path left the fallback to find.
            AudioCapturePipeline.TryDelete(LibraryLayout.SidecarPath(ViewModel.Settings.LibraryFolder, sourcePath, ".paused.json"));
            AudioCapturePipeline.TryDelete(LibraryLayout.LegacySidecarPath(sourcePath, ".paused.json"));
            AudioCapturePipeline.TryDelete(LibraryLayout.LegacyAdjacentPausedPath(sourcePath));
            // Durable half of the same fix: the deletes above are best-effort
            // (TryDelete swallows failures, and a sidecar could be restored by a
            // library move/rename), so record on the clip itself that it has
            // been trimmed. LoadPausedRanges refuses to load ranges at all for a
            // trimmed clip, which is what actually makes the badge impossible
            // rather than merely unlikely.
            var trimmedInfo = ClipInfoSidecar.Load(ViewModel.Settings.LibraryFolder, sourcePath) ?? new ClipInfo(null, null);
            ClipInfoSidecar.Save(ViewModel.Settings.LibraryFolder, sourcePath, trimmedInfo with { IsTrimmed = true });
            _pausedRanges.Clear();
            RefreshPausedBadge();

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

    // ShareDialog is a genuinely separate top-level Window, not an in-window
    // overlay - the Editor's video is a native VLC child window that always
    // paints over Avalonia-rendered siblings regardless of z-order
    // ("airspace", see EditorHoverControls' own comments on this), so an
    // embedded overlay Border rendered behind it while a clip is open,
    // dimming everything except the one thing it was supposed to sit on top
    // of. A separate window sized/positioned to exactly cover this one
    // (done in ShareDialog itself) sits above that airspace the same way the
    // floating hover bar already does.
    // Gates PollEditorHoverControls - the floating bar is a separate always-on-top
    // window over the video, so with Share up it punched through the dimmed
    // backdrop and sat on top of the dialog. Down for as long as Share is
    // open, back on its own the moment the poll sees this clear again.
    private bool _shareDialogOpen;

    private async void ShareButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ShareCurrentClipAsync();
    }

    private async Task ShareCurrentClipAsync()
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        _playback?.Pause();
        ViewModel.IsPlaying = false;
        _shareDialogOpen = true;
        HideEditorHoverControls(immediate: true);
        try
        {
            await new ShareDialog(this, ViewModel).ShowDialog(this);
        }
        finally
        {
            _shareDialogOpen = false;
        }
    }

    internal static string ResolveExportGame(string sourcePath, ClipInfo? sourceInfo)
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
        try
        {
            await dialog.ShowDialog<bool>(this);
        }
        catch (Exception error)
        {
            // ShowDialog throws "Cannot show window with non-visible owner"
            // if the owner isn't in a state Avalonia considers valid at this
            // exact moment (e.g. mid cold-boot transition) - same failure mode
            // ShowEditorHoverControls already guards against. Losing the
            // dialog is better than an unhandled/unobserved exception escaping
            // from here with a half-created native window behind it.
            AppLog.Error($"Failed to show message dialog: {title}", error);
        }
    }

    private async Task<string?> PromptRenameAsync(string currentTitle, string heading = "Rename clip", string watermark = "Clip title")
    {
        var (window, body) = CreateChromelessDialog(heading);

        var textBox = new TextBox
        {
            Text = currentTitle,
            Watermark = watermark
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
            Text = heading,
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
        // Background/BorderThickness/CornerRadius/Foreground/Padding come from
        // the windowChromeButton/windowCloseButton classes now, not local
        // values - those classes carry the flat template that makes the red
        // hover actually paint instead of only flashing on pointer-exit (see
        // AppStyles.axaml). Width/Height stay local: this dialog's 40px
        // titlebar is shorter than the main window's 48px chrome row.
        var closeButton = new Button { Classes = { "windowChromeButton", "windowCloseButton" }, Content = "✕", Width = 40, Height = 40, Margin = new Avalonia.Thickness(0), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
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
        var closeButton = new Button { Classes = { "windowChromeButton", "windowCloseButton" }, Content = "✕", Width = 40, Height = 40, Margin = new Avalonia.Thickness(0), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
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

        StopEditorPlayback(cancelQueuedStart: false, stopMode: PlaybackStopMode.Skip);

        try
        {
            // Reused across editor opens instead of constructing a fresh
            // PlaybackSession every time - PlaybackSession's constructor spins up a
            // whole new LibVLC engine + MediaPlayer, which was the bulk of the
            // "video stays black for a moment" delay on every single clip open.
            // LoadVideoAsync() already fully tears down and replaces the previous
            // Media internally, so the same instance is safe to reuse.
            var playback = _playback ?? new PlaybackSession();
            await playback.LoadVideoAsync(ViewModel.SelectedVideoPath);
            if (cancellationToken.IsCancellationRequested) return;
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
            EditorVideoView.WatchMediaPlayer(playback.VideoPlayer);
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
        catch (OperationCanceledException)
        {
            // Every other cancellation point above is a cooperative
            // "if (cancellationToken.IsCancellationRequested) return;" - this
            // is the one spot (Task.Delay) that throws instead. Being
            // superseded by a newer QueueEditorPlayback (the user opening
            // another clip, or the same one again, before this load settled -
            // routine right after cold boot while the library is still
            // hydrating) is not a failure and must not surface an error
            // dialog for it.
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

    // How this call should deal with the (reused) PlaybackSession itself, as
    // opposed to the view/timer teardown every path does regardless.
    private enum PlaybackStopMode
    {
        // Blocks until libvlc has finished unwinding. Only correct where
        // nothing else is about to touch the session on another thread.
        Synchronous,
        // Fire-and-forget on a worker. For editor CLOSE, where the stop's cost
        // shouldn't freeze the UI and nothing follows it.
        Background,
        // Leave the session alone entirely - the caller is about to run
        // LoadVideoAsync, which stops it itself as the first thing in its own
        // background body, correctly ordered ahead of the media swap.
        Skip
    }

    private void StopEditorPlayback(bool cancelQueuedStart = true, PlaybackStopMode stopMode = PlaybackStopMode.Synchronous)
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
        //
        // Skip exists because backgrounding it is NOT safe on the open path:
        // LoadVideoAsync stops the same session on its own worker, so a
        // fire-and-forget stop raced it with no ordering at all - free to land
        // after the new Media was set, after the view had been re-attached, or
        // in the middle of vout creation, tearing the video output down and
        // leaving it rebuilt at a default size (rendering the clip upscaled and
        // soft rather than at its real resolution).
        if (stopMode == PlaybackStopMode.Background)
        {
            var playback = _playback;
            if (playback is not null) _ = Task.Run(() => playback.Stop());
        }
        else if (stopMode == PlaybackStopMode.Synchronous)
        {
            _playback?.Stop();
        }
        EditorVideoView.WatchMediaPlayer(null);
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
    // Missing sidecar (Legacy backend clips, or no pauses occurred) just
    // means no badge ever shows - not an error.
    private List<(double StartSeconds, double EndSeconds)> LoadPausedRanges(string videoPath)
    {
        // A trimmed clip's pause ranges describe the ORIGINAL recording's
        // timeline, not the shorter file that replaced it, so they can only ever
        // point at the wrong moment - refuse them outright rather than render a
        // "Playback Paused" badge over content that was never paused. See
        // ClipInfo.IsTrimmed for why this is checked here and not left to the
        // best-effort sidecar deletion at trim time.
        if (ViewModel is not null && ClipInfoSidecar.Load(ViewModel.Settings.LibraryFolder, videoPath)?.IsTrimmed == true)
        {
            return new();
        }

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
            var ranges = entries?.Select(e => (StartSeconds: e.start, EndSeconds: e.end)).ToList() ?? new();

            // Catches clips trimmed BEFORE ClipInfo.IsTrimmed existed, which have
            // no flag to go on. Pause offsets belong to the original recording,
            // so a sidecar describing a timeline longer than the file itself can
            // only be left over from before a trim - the ranges cannot be mapped
            // back and the badge they produce is wrong wherever it lands. One
            // second of slack so ordinary rounding between the sidecar's own
            // window and the muxed file's duration is not mistaken for staleness.
            var duration = ViewModel?.Duration ?? TimeSpan.Zero;
            if (duration > TimeSpan.Zero && ranges.Count > 0 &&
                ranges.Max(range => range.EndSeconds) > duration.TotalSeconds + 1)
            {
                AppLog.Info($"Ignoring recording-paused sidecar for {Path.GetFileName(videoPath)}: ranges run to " +
                            $"{ranges.Max(range => range.EndSeconds):0.0}s but the clip is only {duration.TotalSeconds:0.0}s - stale after a trim.");
                return new();
            }

            return ranges;
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

        var scrim = new Border
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
        };
        var overlay = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Topmost = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = scrim
        };
        overlay.Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(overlay, scrim.Background, b => scrim.Background = b);
        overlay.AddHandler(PointerPressedEvent, RecordingPausedOverlay_OnPointerPressed, RoutingStrategies.Tunnel);
        _recordingPausedOverlay = overlay;
        return overlay;
    }

    private void RecordingPausedOverlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Window overlay || !e.GetCurrentPoint(overlay).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        PlayPauseButton_OnClick(this, new RoutedEventArgs());
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
        var wasHidden = !overlay.IsVisible;
        var raised = RepositionPausedOverlay(overlay);
        if (wasHidden) overlay.Show(this);
        // Only when the badge actually claimed the top of the z-band does the
        // hover bar need putting back above it. Doing this unconditionally
        // re-ordered two owned windows on EVERY playback tick (RefreshPausedBadge
        // runs from the playback timer), and that constant reshuffling is what
        // made the bar flicker over the badge. force: the bar itself has not
        // moved, so the skip-if-unchanged check would otherwise return before
        // re-asserting z-order.
        if (raised || wasHidden) RepositionEditorHoverControlsSafe(force: true);
        ApplyCaptureExclusion(overlay, ViewModel.Settings.ExcludeOverlaysFromCapture);
    }

    // Returns true when it actually re-asserted the window's z-order, so the
    // caller knows whether anything else needs putting back on top of it.
    private bool RepositionPausedOverlay(Window overlay)
    {
        // Guarded - PointToScreen can throw while EditorVideoView is
        // momentarily detached from the visual tree (the fullscreen reparent),
        // and this runs from plain event handlers with no timer-level recovery
        // of their own (see the callers in TrackPausedOverlayToWindow).
        try
        {
            var topLeft = EditorVideoView.PointToScreen(new Point(0, 0));
            var bottomRight = EditorVideoView.PointToScreen(new Point(EditorVideoView.Bounds.Width, EditorVideoView.Bounds.Height));
            var width = Math.Max(1, (bottomRight.X - topLeft.X) / overlay.RenderScaling);
            var height = Math.Max(1, (bottomRight.Y - topLeft.Y) / overlay.RenderScaling);
            var handle = NativeHandleOf(overlay);

            // Skip entirely when nothing has actually moved - same
            // skip-if-unchanged guard the hover bar uses, and read from the REAL
            // window rect rather than Avalonia's Position (which just echoes back
            // whatever was last assigned). This runs on every playback timer tick,
            // and re-asserting z-order that often is what made the hover bar
            // above it flicker.
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect) &&
                nativeRect.Left == topLeft.X && nativeRect.Top == topLeft.Y &&
                Math.Abs(overlay.Width - width) < 0.5 && Math.Abs(overlay.Height - height) < 0.5)
            {
                return false;
            }

            overlay.Position = topLeft;
            overlay.Width = width;
            overlay.Height = height;

            // Re-assert the top of the owner's z-band, exactly as the hover bar
            // does (see RepositionEditorHoverControls). Showing an owned window
            // once is not enough: the video renderer's own native child hwnd
            // keeps repainting over it while a clip PLAYS, which hid this badge
            // for precisely the case it exists to report and made it look like
            // it only appeared when playback was paused - pausing just stops
            // the repaints that were covering it. NOACTIVATE so it never takes
            // focus off the editor.
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
                return true;
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Paused overlay reposition failed (recovered)", error);
        }

        return false;
    }

    // Keeps the badge glued to the video area during window drags/resizes -
    // without this its position only updated on playback-timer ticks (and
    // not at all while paused), so it visibly lagged/snapped behind the
    // window instead of moving with it.
    //
    // Each of the three calls below is independently guarded: they run back
    // to back in one plain event handler (not a DispatcherTimer tick, so
    // nothing recovers it for us), and one throwing used to skip the rest for
    // that event - e.g. the paused-overlay reposition failing mid-reparent
    // meant the hover bar's own reposition and the zoom/pan transform update
    // silently never ran for that layout pass either.
    private void TrackPausedOverlayToWindow()
    {
        PositionChanged += (_, _) =>
        {
            if (_recordingPausedOverlay is { IsVisible: true } overlay) RepositionPausedOverlay(overlay);
            RepositionEditorHoverControlsSafe();
        };
        EditorVideoView.LayoutUpdated += (_, _) =>
        {
            if (_recordingPausedOverlay is { IsVisible: true } overlay) RepositionPausedOverlay(overlay);
            RepositionEditorHoverControlsSafe();
            // Covers window resize AND the fullscreen reparent (both change
            // EditorVideoView's rendered height, which the pan-range math
            // depends on) without needing separate handlers for each.
            try
            {
                UpdateVideoTransform();
            }
            catch (Exception error)
            {
                AppLog.Error("Video transform update failed (recovered)", error);
            }
        };
    }

    private void RepositionEditorHoverControlsSafe(bool force = false)
    {
        if (_editorHoverControlsWindow is not { IsVisible: true } hoverBar) return;
        try
        {
            RepositionEditorHoverControls(hoverBar, force);
        }
        catch (Exception error)
        {
            AppLog.Error("Hover bar reposition failed (recovered)", error);
        }
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
        // 120ms was a visible beat of lag either way - the bar took that long
        // to start coming up after the pointer reached the picture, and that
        // long to go away after it left. This is a cursor-rect test and a
        // couple of bounds calls, cheap enough to run at roughly frame rate so
        // both edges feel immediate. The slide-in animation itself is
        // unchanged; it just starts without the wait.
        _hoverControlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        // Guarded: an exception escaping a DispatcherTimer tick kills the
        // subscription, and this touches things that can legitimately throw
        // mid-transition - PointToScreen on a visual that's momentarily
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

    // One rule: the pointer is over the video (or over the bar itself), or the
    // bar goes. Now that the hover bar is opt-in - the docked row under the
    // video is the default - the point of choosing it is keeping the editor
    // clear of controls except while you're actually on the picture, so the
    // zone is the picture and nothing else.
    private void PollEditorHoverControls()
    {
        // Docked mode (the default) is the control row in the timeline panel,
        // so the floating one never comes out at all.
        // IsVisible is the main window's own. Closing ClypDat hides it to the
        // tray rather than exiting, and Avalonia refuses outright to show a
        // window whose owner isn't visible ("Cannot show window with
        // non-visible owner"). Without this the poll threw on that every
        // 120ms for as long as the app sat in the tray, and the recovery path
        // - drop the window, build a fresh one - just produced a new window to
        // fail on, so the bar never came back after a restore.
        if (ViewModel is null || !IsVisible || !ViewModel.IsEditorVisible || ViewModel.IsVideoFullscreen || _playback is null ||
            !ViewModel.Settings.EditorHoverBarEnabled || _shareDialogOpen)
        {
            if (_editorHoverControlsWindow is { IsVisible: true })
            {
                LogHoverControlsState($"hidden (window={IsVisible}, editor={ViewModel?.IsEditorVisible}, fullscreen={ViewModel?.IsVideoFullscreen}, playback={_playback is not null}, hoverBar={ViewModel?.Settings.EditorHoverBarEnabled}, share={_shareDialogOpen})");
            }
            HideEditorHoverControls(immediate: true);
            return;
        }

        // Mid-resize: stay down until the layout stops moving.
        if (DateTime.UtcNow < _hoverControlsSuppressedUntilUtc)
        {
            HideEditorHoverControls(immediate: true);
            return;
        }

        // Windows can hide an owned window itself - owner minimized, owner
        // losing foreground, the focus blips an interactive resize causes -
        // and none of that runs through our Hide(), so Avalonia's IsVisible
        // stays stuck true while the real window is gone. ShowEditorHoverControls
        // only calls Show() again when IsVisible reads false, so that stale
        // true meant the bar was never re-shown for the rest of the session.
        // Believe the OS over our own flag and resync, so the next tick does a
        // genuine Show() instead of the "already visible, just nudge the
        // transform" path forever.
        if (_editorHoverControlsWindow is { IsVisible: true } trackedBar)
        {
            var trackedHandle = NativeHandleOf(trackedBar);
            if (trackedHandle != IntPtr.Zero && !IsWindowVisible(trackedHandle))
            {
                AppLog.Debug("Editor hover bar: native window was hidden by the OS - resyncing so it can be reshown.");
                _hoverControlsSlideOutTimer?.Stop();
                _hoverControlsSlidingOut = false;
                trackedBar.Hide();
                _hoverControlsLastState = string.Empty;
            }
        }

        if (!GetCursorPos(out var cursor)) return;

        // EditorVideoHost, not EditorVideoView: the view carries the zoom
        // ScaleTransform (so its own PointToScreen moves and can extend well
        // outside the visible area once zoomed) and gets reparented into
        // FullscreenVideoHost and back. The host is the stable, untransformed
        // rectangle the video is actually shown in.
        if (EditorVideoHost.Bounds.Width <= 0 || EditorVideoHost.Bounds.Height <= 0)
        {
            // Logged because this path leaves the bar in whatever state it was
            // already in - if it ever gets stuck here, the log says so instead
            // of the bar just silently never coming back.
            LogHoverControlsState("waiting for video bounds");
            return;
        }

        var videoTopLeft = EditorVideoHost.PointToScreen(new Point(0, 0));
        var videoBottomRight = EditorVideoHost.PointToScreen(new Point(EditorVideoHost.Bounds.Width, EditorVideoHost.Bounds.Height));
        var overVideo = cursor.X >= videoTopLeft.X && cursor.X < videoBottomRight.X
                        && cursor.Y >= videoTopLeft.Y && cursor.Y < videoBottomRight.Y;

        // The bar is its own top-level window hanging at the video's bottom
        // edge, and can extend a pixel past the pane it belongs to, so it's
        // checked separately rather than assumed to be inside the zone.
        //
        // Asked of Windows rather than worked out from Position/Width/scaling.
        // Whatever window is under the cursor is the authority on whether the
        // pointer is on the bar - the arithmetic version could disagree with
        // what the user is actually pointing at (it read Avalonia's cached
        // geometry and re-derived DIP/physical scaling), and disagreeing for a
        // single tick while someone is clicking a control is what made the bar
        // drop out from under the click.
        var barHandle = NativeHandleOf(_editorHoverControlsWindow);
        var overBar = barHandle != IntPtr.Zero
                      && _editorHoverControlsWindow is { IsVisible: true }
                      && GetAncestor(WindowFromPoint(cursor), GaRoot) == barHandle;

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
            // Animated both ways. "Instant" here is about it starting to go
            // the moment the pointer leaves (HoverControlsGrace is zero), not
            // about skipping the slide - the slide down is the exit.
            HideEditorHoverControls(immediate: false);
        }
    }

    // Called from every path that changes the video pane's rect out from
    // under the bar - a window resize today, and safe to call from anywhere
    // else that has the same effect.
    private void SuspendHoverControlsForResize()
    {
        _hoverControlsSuppressedUntilUtc = DateTime.UtcNow + HoverControlsResizeSettle;
        if (_editorHoverControlsWindow is { IsVisible: true })
        {
            LogHoverControlsState("suspended for resize");
            HideEditorHoverControls(immediate: true);
        }
    }

    private void ShowEditorHoverControls()
    {
        // Cancels an in-flight slide-out: moving back over the video during
        // the 190ms exit brings the bar straight back rather than letting it
        // finish leaving and then reappear.
        _hoverControlsSlideOutTimer?.Stop();
        _hoverControlsSlidingOut = false;

        var window = EnsureEditorHoverControlsWindow();
        // Every tick, not just on the hidden->shown transition - see
        // RepositionEditorHoverControls, which no-ops unless the video pane
        // has actually moved or resized, so this costs nothing while the bar
        // just sits there but keeps it glued to the video during a resize.
        RepositionEditorHoverControls(window);
        if (!window.IsVisible)
        {
            // Parked below the window's own bounds so the first frame after
            // Show is already off-screen; flipping it back one frame later
            // gives the transition a "from" state to animate out of, rather
            // than both values landing in the same layout pass.
            SetHoverControlsOffset(HoverControlsSlideDistance);
            try
            {
                window.Show(this);
            }
            catch (Exception error)
            {
                // A Window that has been closed (rather than hidden) throws
                // here and can never be shown again - one of those and the bar
                // would be gone for the rest of the session. Drop the dead
                // reference so a fresh one gets built.
                //
                // Backing off matters as much as the rebuild: whatever made
                // Show fail is usually still true a tick later, and retrying
                // at 8/sec turned one real problem into thousands of log
                // lines. A second between attempts still recovers promptly.
                AppLog.Error("Editor hover bar show failed; rebuilding it", error);
                _editorHoverControlsWindow = null;
                _hoverControlsBackdrop = null;
                _hoverControlsLastState = string.Empty;
                _hoverControlsSuppressedUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                return;
            }

            // Deliberately no ApplyCaptureExclusion here: "exclude overlays
            // from capture" is meant for the HUD-style notifications (clip
            // saved, recording paused, etc.) that float over a game, not for
            // this bar's own playback controls - those are editor UI the
            // user is actively using, and excluding it made the whole bar
            // vanish from screen shares/recordings of the app too, not just
            // from whatever the setting was meant to hide.

            // Everything RepositionEditorHoverControls assigned above went to
            // a window that had no native hwnd yet. Re-apply now that Show has
            // made one, bypassing the skip-if-unchanged check (which would
            // otherwise see Avalonia's already-correct Position and do
            // nothing), so the bar is guaranteed to be where the video is.
            RepositionEditorHoverControls(window, force: true);
            // Applied after Show, since there's no hwnd to set styles on
            // before it - keeps clicking a control from stealing activation
            // and taking the bar down out from under the click.
            MakeWindowNonActivating(window);
            LogHoverControlsState($"sliding in ({DescribeNativeWindow(window)})");
            Dispatcher.UIThread.Post(() => SetHoverControlsOffset(0), DispatcherPriority.Loaded);
        }
        else
        {
            SetHoverControlsOffset(0);
        }
    }

    // What the OS says about the window, for the log - Avalonia's own
    // IsVisible/Position can't distinguish "shown where we asked" from "shown
    // somewhere else" or "not actually shown at all".
    private static string DescribeNativeWindow(Window window)
    {
        var handle = NativeHandleOf(window);
        if (handle == IntPtr.Zero) return "no native handle";
        var visible = IsWindowVisible(handle);
        return GetWindowRect(handle, out var rect)
            ? $"native={visible}, rect={rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}"
            : $"native={visible}, rect=unavailable";
    }

    // immediate: true for leaving the editor or entering fullscreen (the bar
    // has no business animating out of a view that's already gone); false for
    // the pointer moving off the video, which slides it away.
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

    // Sizes and places the bar against the video pane as it currently is.
    // Safe to call on every poll tick: it works out the target geometry and
    // returns without touching the native window unless something actually
    // moved. That's what keeps the bar matched to the video through window
    // resizes, maximise/restore, a display-scale change or the pan slider
    // appearing - the LayoutUpdated hook alone missed cases where the video
    // pane's rect changed without EditorVideoView itself re-laying out.
    private void RepositionEditorHoverControls(Window bar, bool force = false)
    {
        // Same reasoning as PollEditorHoverControls - position against the
        // untransformed host, not the zoom-transformed/reparented view.
        if (EditorVideoHost.Bounds.Width <= 0 || EditorVideoHost.Bounds.Height <= 0) return;
        var topLeft = EditorVideoHost.PointToScreen(new Point(0, 0));
        var width = Math.Max(1, EditorVideoHost.Bounds.Width);
        // 38 for the controls row plus the 14px scrub strip above it. The row
        // clears the 34px buttons with a little to spare; the strip keeps its
        // height because it is the seek hit target, and thinning that makes
        // the bar harder to use rather than just slimmer.
        const double barHeight = 52;
        var bottomOnScreen = EditorVideoHost.PointToScreen(new Point(0, EditorVideoHost.Bounds.Height));
        // The OWNER's scaling, not the bar's. Position is in physical pixels
        // while Height is in DIPs, so converting between them needs the real
        // scale factor of the display this is on - and a Window that has not
        // been shown yet reports RenderScaling 1.0 regardless. On a 200%
        // display that put the bar half its own height too low on the very
        // first show, hanging past the bottom of the video pane.
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        var position = new PixelPoint(topLeft.X, bottomOnScreen.Y - (int)(barHeight * scaling));
        var handle = NativeHandleOf(bar);

        // The skip-if-unchanged check reads the REAL window rect, not
        // bar.Position. This method runs before Show() while the window is
        // still hidden, and Avalonia's Position just echoes back whatever was
        // last assigned to it - so once a hidden-window assignment failed to
        // reach the OS, "already in the right place, nothing to do" was true
        // forever while the actual window sat at a stale spot (its pre-resize
        // rect, or wherever Windows defaulted it on Show). That is the bar
        // reading as shown by every state check and still not being where the
        // user is looking.
        if (!force && handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect))
        {
            if (nativeRect.Left == position.X && nativeRect.Top == position.Y &&
                Math.Abs(bar.Width - width) < 0.5 && Math.Abs(bar.Height - barHeight) < 0.5)
            {
                return;
            }
        }

        bar.Width = width;
        bar.Height = barHeight;
        bar.Position = position;

        // Drive the move through Win32 too, so it lands whether or not
        // Avalonia decides to flush a Position set on a hidden window - and
        // take the top of the owner's z-band in the same call (NOACTIVATE, so
        // it never steals focus) so the native video child hwnd can't end up
        // painting over the bar after a resize or alt-tab reorders things.
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HwndTop, position.X, position.Y, 0, 0, SwpNoSize | SwpNoActivate);
        }
    }

    // Contents of the floating hover bar. The docked alternative is plain XAML
    // in the timeline panel (it predates this and is deliberately unchanged) -
    // this one carries the extras that only make sense floating over the
    // picture, the scrub strip and the elapsed/duration readout, since in
    // docked mode the timeline right below already provides both.
    private Control BuildPlaybackBarLayout()
    {
        PathIcon Icon(string data, double size = 16) => new()
        {
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(Color.Parse("#C8D6E6")),
            Data = Geometry.Parse(data),
        };

        Button TransportButton(string data, EventHandler<RoutedEventArgs> onClick, string? tip = null)
        {
            var button = new Button { Classes = { "transportButton", "flatControl" }, Content = Icon(data) };
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
        var playPauseButton = new Button { Classes = { "playButton", "flatControl" }, Content = new Grid { Children = { playIcon, pauseIcon } } };
        playPauseButton.Click += PlayPauseButton_OnClick;

        var muteIcon = new PathIcon { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        muteIcon.Bind(PathIcon.ForegroundProperty, new Binding("IsMasterMuted") { Converter = BoolToMuteBrushConverter.Instance });
        muteIcon.Bind(PathIcon.DataProperty, new Binding("EffectiveMasterVolumePercent") { Converter = VolumeLevelToIconConverter.Instance });
        var muteToggle = new Border
        {
            Classes = { "muteToggle", "flatControl" },
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
            // The Slider template's track isn't vertically centred within the
            // control's own bounds, so VerticalAlignment.Center still leaves
            // the rail sitting low against the icons beside it. Nudged up to
            // line the rail up with them - this is on top of whatever the row
            // itself is offset by, since it corrects the template, not the row.
            //
            // Centred content moves by HALF the margin, so these values step
            // in 2s.
            Margin = new Thickness(0, -1, 0, 0),
        };
        volumeSlider.Bind(Slider.ValueProperty, new Binding("MasterVolumePercent", BindingMode.TwoWay));
        volumeSlider.Bind(OpacityProperty, new Binding("IsMasterMuted") { Converter = BoolToOpacityConverter.Instance });

        // Bumped a point and lightened from the original #5C6D7E/#8C98A7 -
        // both read as too dim/small against the bar's dark scrim, especially
        // over a bright part of the video underneath.
        var timeText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#EDF4FB")), FontSize = 14, FontWeight = FontWeight.Bold, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        timeText.Bind(TextBlock.TextProperty, new Binding("CurrentTimeLabel"));
        var slashText = new TextBlock { Text = " / ", Foreground = new SolidColorBrush(Color.Parse("#8C98A7")), FontSize = 14, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        var durationText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#B7C4D2")), FontSize = 14, FontFamily = "Consolas", VerticalAlignment = VerticalAlignment.Center };
        durationText.Bind(TextBlock.TextProperty, new Binding("DurationLabel"));

        // Nothing but the five transport buttons, so the row is symmetric about
        // Play/Pause and centring the group centres that button. The time
        // readout used to live on the end here, which made the run right-heavy
        // and left Play sitting off to the left of centre - it sits with the
        // volume group now instead.
        var transportGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Steps in 2s - centred content shifts by half the margin.
            Margin = new Thickness(0, -4, 0, 0),
            Children =
            {
                TransportButton("M6 6h2v12H6zm3.5 6l8.5 6V6z", RestartButton_OnClick, "Restart"),
                TransportButton("M16 5v14L5 12Z", StepBackButton_OnClick, "Step back"),
                playPauseButton,
                TransportButton("M8 5v14l11-7z", StepForwardButton_OnClick, "Step forward"),
                TransportButton("M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z", EndButton_OnClick, "End"),
            },
        };

        // Percentage and Reset mirror the fullscreen bar exactly - the two bars
        // control the same master volume, so having the readout in one and a
        // bare slider in the other made the same action feel like two features.
        // Reset is disabled rather than hidden at 100%, matching that bar's
        // reasoning: collapsing it would shuffle everything beside it.
        var volumePercentText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C8D6E6")),
            FontSize = 12,
            Width = 30,
            // Steps in 2s for the same reason as volumeSlider's margin above -
            // centred content shifts by half the margin.
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        volumePercentText.Bind(TextBlock.TextProperty, new Binding("MasterVolumePercent") { StringFormat = "{0:0}%" });

        var volumeResetButton = new Button
        {
            Classes = { "linkButton" },
            Content = "Reset",
            FontSize = 11,
            Padding = new Thickness(6, 1),
            // Steps in 2s, same half-margin rule as the rail and percentage.
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        volumeResetButton.Bind(IsEnabledProperty, new Binding("IsMasterVolumeNonDefault"));
        volumeResetButton.Click += MasterVolumeReset_OnClick;
        ToolTip.SetTip(volumeResetButton, "Reset to 100%");

        // The volume run is lifted as a unit rather than element by element, so
        // the mute tile, rail and percentage keep the alignment they already
        // have relative to each other (their own margins still apply on top of
        // this). The time readout beside it is deliberately left where it is.
        // Half-margin rule as elsewhere in this bar: -8 raises it 4px.
        var volumeControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0),
            Children = { muteToggle, volumeSlider, volumePercentText, volumeResetButton },
        };

        var volumeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Children = { timeText, slashText, durationText },
                },
                volumeControls,
            },
        };

        var fullscreenButton = TransportButton("M7,14H5v5h5v-2H7V14z M5,10h2V7h3V5H5V10z M17,17h-3v2h5v-5h-2V17z M14,5v2h3v3h2V5H14z", FullscreenButton_OnClick, "Fullscreen");
        fullscreenButton.HorizontalAlignment = HorizontalAlignment.Right;

        // Scrub strip along the top edge, same control (and same handler) the
        // fullscreen bar uses. Seeking previously meant leaving the picture for
        // the timeline panel below, even for a small nudge. The 14px Border is
        // the hit target; the 3px bar inside it is hit-test invisible so a
        // click anywhere in that band seeks rather than only a hit on the rail.
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Height = 3,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            // Top, not Center: centering it in the 14px hit strip left a band
            // of scrim sitting above the progress line, so the bar looked like
            // it started with an empty grey strip instead of with the
            // playback line itself. The strip keeps its full height as a hit
            // target, the line just sits flush with the bar's top edge.
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            Foreground = Application.Current?.Resources["AccentBrush"] as IBrush ?? new SolidColorBrush(Color.Parse("#5864E8")),
            IsHitTestVisible = false,
        };
        progressBar.Bind(ProgressBar.MaximumProperty, new Binding("Duration.TotalSeconds"));
        progressBar.Bind(ProgressBar.ValueProperty, new Binding("CurrentTime.TotalSeconds"));

        var progressStrip = new Border
        {
            Height = 14,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = progressBar,
        };
        progressStrip.PointerPressed += FullscreenProgressBar_OnPointerPressed;

        // One cell with all three groups stacked in it, each aligned to its own
        // edge, rather than three columns. In a three-column split the centre
        // column is only centred between the other two, so the transport row
        // sat visibly off-centre by however much the volume group outweighs
        // the lone fullscreen button. Overlapping them in a single cell centres
        // the transport on the bar itself; at any width the video pane actually
        // gets there is far more room than the three groups need.
        // Negative top rather than trimming the bottom, now the bar is slim
        // enough that the controls row has little spare height: taking it off
        // the bottom would shrink the space the 34px buttons have to fit in
        // and start squeezing them, where a negative top gives the row MORE
        // room and still moves the centre up by half the offset. The controls
        // otherwise centre in the band below the progress strip, which leaves
        // them sitting low against the scrim as a whole.
        var layout = new Grid { Margin = new Thickness(14, -6, 14, 0) };
        layout.Children.Add(volumeGroup);
        layout.Children.Add(transportGroup);
        layout.Children.Add(fullscreenButton);

        var barContent = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(progressStrip, 0);
        Grid.SetRow(layout, 1);
        barContent.Children.Add(progressStrip);
        barContent.Children.Add(layout);
        return barContent;
    }

    private Window EnsureEditorHoverControlsWindow()
    {
        if (_editorHoverControlsWindow is not null) return _editorHoverControlsWindow;

        var backdrop = new Border
        {
            // Translucent scrim behind the whole row, not an opaque plate -
            // the picture still reads through it, it just gets knocked back
            // far enough that the controls sit on a consistent surface
            // instead of fighting whatever frame is underneath. The progress
            // strip along the top edge is what separates it from the video,
            // so there's no border line here.
            Background = new SolidColorBrush(Color.Parse("#8C0B1016")),
            Child = BuildPlaybackBarLayout(),
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
        window.Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(window, backdrop.Background, b => backdrop.Background = b);
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

    // Ground truth for the owned overlay windows. Avalonia's Window.IsVisible
    // and Window.Position are its OWN bookkeeping - they report back what we
    // last assigned, whether or not the native window ever agreed. Every
    // "the bar is showing per every state check but isn't on screen" symptom
    // comes from trusting those two over what the OS actually did, so the
    // hover-bar paths below check and drive the real hwnd instead.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GaRoot = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExTransparent = 0x00000020L;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WdaNone = 0x00000000;
    // Excluded from capture entirely - the window still renders on the physical
    // display, it just isn't composited into anything that asks DWM for the
    // screen. Deliberately NOT the older WDA_MONITOR (0x01), which goes back to
    // Windows 7 but paints the window as a black rectangle in captures instead
    // of omitting it: a black box in the middle of a clip is worse than the
    // overlay simply being there.
    private const uint WdaExcludeFromCapture = 0x00000011;

    // Same call KeePassXC uses to keep its window out of screenshots. Requires
    // Windows 10 2004 (build 19041) - SupportedOSPlatformVersion here is 17763,
    // so on anything older this fails and the overlay captures as it always
    // did. Applied on every show rather than once at construction: the setting
    // is live, and the flag has to be cleared again when it's turned off.
    private static void ApplyCaptureExclusion(Window? window, bool exclude)
    {
        var handle = NativeHandleOf(window);
        if (handle == IntPtr.Zero) return;
        if (!SetWindowDisplayAffinity(handle, exclude ? WdaExcludeFromCapture : WdaNone) && exclude)
        {
            AppLog.Debug($"Overlay capture exclusion unavailable (needs Windows 10 build 19041): error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
        }
    }

    private static IntPtr NativeHandleOf(Window? window) =>
        window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    // Clicking anything in an overlay would otherwise activate that window,
    // which deactivates the main window - and the main window's Deactivated
    // handler takes the overlays down. The click landed on a bar that was
    // being hidden underneath it, so it read as a flash and the button had to
    // be pressed a second time. WS_EX_NOACTIVATE means clicking never moves
    // activation in the first place; mouse input still routes normally, it
    // just doesn't steal focus.
    private static void MakeWindowNonActivating(Window window)
    {
        var handle = NativeHandleOf(window);
        if (handle == IntPtr.Zero) return;
        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        if ((exStyle & WsExNoActivate) != 0) return;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExNoActivate));
    }

    // WS_EX_TRANSPARENT on top of the above: the clip overlay's window now
    // stretches from the badge out to the screen edge to give the slide a
    // runway, and most of that strip is empty and fully transparent. Without
    // this it would still hit-test, so for the couple of seconds the overlay is
    // up, clicks landing in that empty strip would hit nothing instead of
    // reaching the window underneath. Purely decorative window, no input to
    // lose by making the whole thing click-through.
    private static void MakeWindowClickThrough(Window window)
    {
        var handle = NativeHandleOf(window);
        if (handle == IntPtr.Zero) return;
        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        if ((exStyle & WsExTransparent) != 0) return;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExTransparent));
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
            // LibVLC ends video independently from the WASAPI mixer. Stop the
            // mixer too before leaving the editor in its ended state; otherwise
            // its buffered audio can overlap the next seek/play.
            _playback.Pause();
            ViewModel.IsPlaying = false;
            _playbackTimer.Stop();
            _endedAtTrimBoundary = true;
        }
    }

    // Where along the clip the pointer currently is, ignoring any grab offset.
    private double PointerMilliseconds(PointerEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return 0;
        var width = Math.Max(1, TimelineSurface.Bounds.Width);
        return ViewModel.Duration.TotalMilliseconds * Math.Clamp(e.GetPosition(TimelineSurface).X / width, 0, 1);
    }

    // Captured when a trim handle is grabbed, so the boundary moves WITH the
    // pointer rather than jumping to it - see _trimDragGrabOffsetMs.
    private double TrimGrabOffsetMs(PointerEventArgs e, TimeSpan boundary) =>
        boundary.TotalMilliseconds - PointerMilliseconds(e);

    private void UpdateTimelineFromPointer(PointerEventArgs e, TimelineDragMode mode)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        var point = e.GetPosition(TimelineSurface);
        var width = Math.Max(1, TimelineSurface.Bounds.Width);
        var time = TimeSpan.FromMilliseconds(ViewModel.Duration.TotalMilliseconds * Math.Clamp(point.X / width, 0, 1));
        // Trim drags carry the grab offset; the playhead does not (clicking the
        // surface means "seek here", so it is always zero for that mode).
        var trimTime = TimeSpan.FromMilliseconds(Math.Clamp(
            PointerMilliseconds(e) + _trimDragGrabOffsetMs, 0, ViewModel.Duration.TotalMilliseconds));
        switch (mode)
        {
            case TimelineDragMode.TrimStart:
                ViewModel.TrimStart = trimTime;
                ViewModel.CurrentTime = ViewModel.TrimStart;
                ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
                break;
            case TimelineDragMode.TrimEnd:
                ViewModel.TrimEnd = trimTime;
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

        // Width of the visible line, which is NOT the handle Border's own width -
        // that is deliberately wider and transparent to make the handle easier to
        // grab (see the XAML). All the placement below works in terms of the line,
        // then shifts the Border by the padding either side of it so the line still
        // lands exactly on the trim boundary.
        const double barWidth = 4;
        var hitPadding = Math.Max(0, (TrimEndHandle.Width - barWidth) / 2);

        // Sits entirely on the excluded side of the boundary (flush against
        // it, not centered on it) - "thicker" toward the left for the start
        // handle and toward the right for the end handle, like a bracket
        // hugging the selected range from outside instead of overlapping it.
        var startMaxLeft = Math.Max(0, width - barWidth);
        var startLeft = Math.Clamp(start - barWidth, 0, startMaxLeft);
        Canvas.SetLeft(TrimStartHandle, startLeft - hitPadding);
        Canvas.SetTop(TrimStartHandle, 0);
        TrimStartHandle.Height = videoLaneHeight;
        Canvas.SetLeft(TrimStartCap, startLeft - (capWidth - barWidth) / 2);
        Canvas.SetTop(TrimStartCap, -7);

        var endMaxLeft = Math.Max(0, width - barWidth);
        var endLeft = Math.Clamp(end, 0, endMaxLeft);
        Canvas.SetLeft(TrimEndHandle, endLeft - hitPadding);
        Canvas.SetTop(TrimEndHandle, 0);
        TrimEndHandle.Height = videoLaneHeight;
        Canvas.SetLeft(TrimEndCap, endLeft - (capWidth - barWidth) / 2);
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
    internal static async Task<ProcessResult> RunProcessWithProgressAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan totalDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool background = false)
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

        // Share encodes run while the user may well be mid-game with the
        // replay buffer recording, so they yield rather than compete. Export
        // and Save Trim are deliberate foreground actions the user is sitting
        // and waiting on, so they keep normal priority.
        if (background)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* already exited, or the OS refused - not worth failing the encode over */ }
        }

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

    internal static string FormatEta(TimeSpan remaining)
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
    private static (Window Window, Panel Body) CreateChromelessDialog(string titleBarLabel, bool centerTitle = false)
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
        if (centerTitle)
        {
            // No icon, just the label spanning the middle column - matches
            // dialogs (e.g. the new-clips popup) whose title is itself the
            // headline rather than a small caption beside the app icon.
            var centeredTitleText = new TextBlock
            {
                Text = titleBarLabel,
                Foreground = Avalonia.Media.Brush.Parse("#B9C6D4"),
                FontSize = 14,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(centeredTitleText, 1);
            titleBar.Children.Add(centeredTitleText);
        }
        else
        {
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
            titleBar.Children.Add(titleLeft);
        }

        var closeButton = new Button
        {
            Classes = { "windowChromeButton", "windowCloseButton" },
            Content = "✕",
            Width = 40,
            Height = 40,
            Margin = new Avalonia.Thickness(0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
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

    internal sealed record ProcessResult(int ExitCode, string Output, string Error);
}
