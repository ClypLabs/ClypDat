using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
    private IReplayBuffer? _replayBuffer;
    private ReplayBackendOption _activeReplayBackend = ReplayBackendOption.Auto;
    private GlobalHotkeyService? _globalHotkey;
    private readonly HashSet<string> _capturedHotkeyKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _replayTransitioning;
    private bool _replayArmed;
    private int _clipSaving;
    private bool _updateDialogOpen;
    private LibVLC? _hoverPreviewLibVlc;
    private MediaPlayer? _hoverPreviewMediaPlayer;
    private ClipCardViewModel? _hoverPreviewClip;
    private ClipCardViewModel? _pendingHoverClip;
    private Control? _pendingHoverBorder;
    private Control? _hoverPreviewBorder;
    private static readonly TimeSpan HoverPreviewSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HoverPreviewStartCooldown = TimeSpan.FromMilliseconds(250);
    private DateTime _lastHoverPreviewStartUtc = DateTime.MinValue;
    private readonly DispatcherTimer _hoverPreviewDelay = new() { Interval = HoverPreviewSettleDelay };
    // The hover-preview VideoView hosts a native child window (HWND on Windows).
    // Whenever the cursor is directly over it, Win32 routes raw mouse input to
    // that child window instead of the card underneath, so Avalonia's
    // PointerExited on the card can fire spuriously (or fail to fire at all) -
    // neither is trustworthy for deciding when to stop the preview. Polling the
    // real OS cursor position against the card's actual screen bounds sidesteps
    // that entirely.
    private readonly DispatcherTimer _hoverWatchdog = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow()
    {
        InitializeComponent();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playbackTimer.Tick += (_, _) => SyncPlaybackPosition();
        _gameDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameDetectionTimer.Tick += (_, _) => UpdateDetectedGame();
        // Each real StartHoverPreview() does a synchronous native Stop/Media-swap/Play
        // on the UI thread. The settle delay alone only debounces "did the cursor stop
        // moving" - it doesn't stop a fast swipe across many cards from firing one of
        // these native calls every ~120ms, which is fast enough to pile up and hang the
        // whole window (stress-testing hovers reproduced this as a genuine UI-thread
        // freeze, not a crash). This cooldown makes sure consecutive real starts are at
        // least HoverPreviewStartCooldown apart, rescheduling itself rather than
        // dropping the request so the cursor's final resting card still always wins.
        _hoverPreviewDelay.Tick += (_, _) =>
        {
            _hoverPreviewDelay.Stop();
            if (_pendingHoverClip is null || _pendingHoverBorder is null) return;

            var sinceLastStart = DateTime.UtcNow - _lastHoverPreviewStartUtc;
            if (sinceLastStart < HoverPreviewStartCooldown)
            {
                _hoverPreviewDelay.Interval = HoverPreviewStartCooldown - sinceLastStart;
                _hoverPreviewDelay.Start();
                return;
            }

            _hoverPreviewDelay.Interval = HoverPreviewSettleDelay;
            var clip = _pendingHoverClip;
            var border = _pendingHoverBorder;
            _pendingHoverClip = null;
            _pendingHoverBorder = null;
            StartHoverPreview(clip, border);
        };
        _hoverWatchdog.Tick += (_, _) => CheckHoverWatchdog();
        Opened += (_, _) =>
        {
            ApplySavedWindowBounds();
            ViewModel?.UpdateCardLayout(Bounds.Width);
            InitializeReplayServices();
            UpdateDetectedGame();
            _gameDetectionTimer.Start();
            _ = EnsureLibraryFolderAsync();
            _ = CheckForUpdatesAsync();
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
        KeyUp += MainWindow_OnKeyUp;
        KeyDown += MainWindow_OnKeyDown;
        Closing += (_, _) =>
        {
            SaveWindowBounds();
            ViewModel?.SaveSettings();
        };
        Closed += (_, _) =>
        {
            _globalHotkey?.Dispose();
            _cs2GsiListener?.Dispose();
            _gameDetectionTimer.Stop();
            _hoverPreviewDelay.Stop();
            DisposeHoverPreview();
            _hoverPreviewLibVlc?.Dispose();
            if (_replayBuffer is not null) _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
            _replayBuffer?.Dispose();
            _playback?.Dispose();
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

        if (ViewModel.Settings.StartReplayOnLaunch)
        {
            _replayArmed = true;
            ViewModel.RecorderStatus = "Replay Armed";
            UpdateDetectedGame();
        }
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

    private async void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video files")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await ViewModel!.OpenVideoFileAsync(path);
            QueueEditorPlayback();
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
        if (Interlocked.CompareExchange(ref _clipSaving, 1, 0) != 0) return;
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

                AppLog.Info(isAutoClip ? $"Auto-clip triggered: {autoClipLabel}." : "Replay clip save requested.");

                // Windows Capture segments need a few seconds to concat/mux before
                // the clip lands in the library, so give instant feedback on the
                // hotkey press instead of waiting for that to finish.
                var notifiedEarly = _replayBuffer is WindowsReplayBuffer;
                if (notifiedEarly) ShowClipSavedNotification();

                var outputPath = await Task.Run(() => _replayBuffer.SaveReplayAsync(outputFolder, titleOverride: autoClipLabel));
                AppLog.Info($"Replay clip saved: {outputPath}");
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
            Interlocked.Exchange(ref _clipSaving, 0);
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
        Directory.CreateDirectory(Path.Combine(path, "Saved Clips"));
        await ViewModel.LoadLibraryFolderAsync(path);
    }

    // First run: EVE gets a Videos\EVE folder with no prompt, so clips just start
    // landing somewhere sane instead of blocking recording behind a folder picker.
    // "Saved Clips" underneath is the default export destination.
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
        if (sender is not Control { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        e.Handled = true;
        await OpenClipCardAsync(clip);
    }

    private DateTime _lastCardOpenUtc = DateTime.MinValue;

    // Shared by the normal PointerPressed path and CheckHoverWatchdog's native-HWND
    // click-through path (see there) - debounced so a click that both paths happen
    // to notice doesn't open the same clip twice.
    private async Task OpenClipCardAsync(ClipCardViewModel clip)
    {
        if (ViewModel is null) return;
        if (DateTime.UtcNow - _lastCardOpenUtc < TimeSpan.FromMilliseconds(400)) return;
        _lastCardOpenUtc = DateTime.UtcNow;
        await ViewModel.OpenClipAsync(clip);
        QueueEditorPlayback();
    }

    private void ClipCard_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: ClipCardViewModel clip } border) return;
        clip.IsHovered = true;
        if (ViewModel?.EnableClipHoverPreview != true || _hoverPreviewClip == clip) return;

        _pendingHoverClip = clip;
        _pendingHoverBorder = border;
        _hoverPreviewDelay.Stop();
        _hoverPreviewDelay.Interval = HoverPreviewSettleDelay;
        _hoverPreviewDelay.Start();
    }

    private void ClipCard_OnPointerExited(object? sender, PointerEventArgs e)
    {
        // Deliberately not stopping the preview here - see _hoverWatchdog's
        // comment. This only cancels a not-yet-started pending hover and resets
        // the highlight; the watchdog poll is what actually decides when to stop.
        if (sender is not Control { DataContext: ClipCardViewModel clip }) return;
        clip.IsHovered = false;
        if (_pendingHoverClip == clip)
        {
            _pendingHoverClip = null;
            _pendingHoverBorder = null;
            _hoverPreviewDelay.Stop();
        }
    }

    private void CheckHoverWatchdog()
    {
        if (_hoverPreviewBorder is null || !_hoverPreviewBorder.IsAttachedToVisualTree())
        {
            StopHoverPreview();
            return;
        }

        if (!GetCursorPos(out var cursor)) return;
        var topLeft = _hoverPreviewBorder.PointToScreen(new Point(0, 0));
        var bottomRight = _hoverPreviewBorder.PointToScreen(new Point(_hoverPreviewBorder.Bounds.Width, _hoverPreviewBorder.Bounds.Height));
        var inside = cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
        if (!inside)
        {
            StopHoverPreview();
            return;
        }

        // The hover-preview VideoView is a real native child HWND, so a click that
        // lands on it never reaches Avalonia's input pipeline at all - Windows
        // routes the mouse message straight to that child window, bypassing both
        // ClipCard_OnPointerPressed and the selection checkbox's own Click handler
        // (the checkbox sits in that same corner and gets covered too).
        // GetAsyncKeyState's low bit latches "was this key pressed since the last
        // call", so it still catches a quick click even at this timer's interval.
        if (_hoverPreviewClip is { } clip && (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0)
        {
            var checkBox = _hoverPreviewBorder.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(box => box.DataContext == clip);
            if (checkBox is not null && checkBox.Bounds.Width > 0)
            {
                var checkTopLeft = checkBox.PointToScreen(new Point(0, 0));
                var checkBottomRight = checkBox.PointToScreen(new Point(checkBox.Bounds.Width, checkBox.Bounds.Height));
                if (cursor.X >= checkTopLeft.X && cursor.X <= checkBottomRight.X && cursor.Y >= checkTopLeft.Y && cursor.Y <= checkBottomRight.Y)
                {
                    ViewModel?.SetClipSelected(clip, !clip.IsSelected);
                    return;
                }
            }

            _ = OpenClipCardAsync(clip);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private const int VK_LBUTTON = 0x01;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private Media? _hoverPreviewMedia;
    private Media? _hoverPreviewMediaPendingDispose;

    // A fresh MediaPlayer per hover (and disposing it on every exit) was blocking
    // the UI thread hard enough to trip Windows' "(Not Responding)" state,
    // especially when the mouse crossed several cards quickly - MediaPlayer
    // construction/disposal binds/unbinds a native video output surface, which
    // isn't cheap. Fixed by creating the MediaPlayer once and reusing it for
    // every hover (only swapping which clip's Media it plays), so a hover only
    // ever costs a Media swap + Play/Stop instead of a full player teardown.
    private MediaPlayer EnsureHoverPreviewPlayer()
    {
        if (_hoverPreviewMediaPlayer is not null) return _hoverPreviewMediaPlayer;
        _hoverPreviewLibVlc ??= new LibVLC("--quiet");
        _hoverPreviewMediaPlayer = new MediaPlayer(_hoverPreviewLibVlc) { Mute = true, Volume = 0 };
        _hoverPreviewMediaPlayer.EndReached += HoverPreview_OnEndReached;
        return _hoverPreviewMediaPlayer;
    }

    // The hover-preview VideoView renders through a real native child HWND (LibVLC's
    // windowed output), which Windows hit-tests and routes mouse messages to
    // directly - Avalonia's own input pipeline never sees a message that lands on
    // it, so the card's PointerPressed and the selection checkbox's Click both go
    // silent while a preview is playing over them. Polling for the click
    // (CheckHoverWatchdog + GetAsyncKeyState) was the first attempt at working
    // around this and proved unreliable in practice. The actual fix: subclass that
    // HWND's window procedure to answer WM_NCHITTEST with HTTRANSPARENT, which
    // tells Windows "nothing here" and makes it continue hit-testing past this
    // window to whatever's underneath (the real Avalonia surface) - clicks land on
    // the card/checkbox normally again. This is a per-window subclass of a window
    // this process itself owns, not a system-wide input hook (see
    // GlobalHotkeyService's comment on why hooks are avoided here - anti-cheat
    // flags them), so it carries none of that risk. The player (and its Hwnd) is
    // reused across every hover, so this only needs to run once.
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTTRANSPARENT = -1;
    private WndProcDelegate? _hoverVideoWndProc;
    private IntPtr _hoverVideoOriginalWndProc;
    private IntPtr _hoverVideoSubclassedHwnd;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void EnsureHoverVideoClickThrough(MediaPlayer player)
    {
        var hwnd = player.Hwnd;
        if (hwnd == _hoverVideoSubclassedHwnd)
        {
            AppLog.Info($"Hover preview click-through: hwnd={hwnd} already subclassed.");
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            AppLog.Info("Hover preview click-through: player.Hwnd is zero, skipping subclass this time.");
            return;
        }

        _hoverVideoWndProc = (hWnd, msg, wParam, lParam) =>
            msg == WM_NCHITTEST
                ? (IntPtr)HTTRANSPARENT
                : CallWindowProc(_hoverVideoOriginalWndProc, hWnd, msg, wParam, lParam);

        var previous = SetWindowLongPtr(hwnd, GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hoverVideoWndProc));
        var win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero)
        {
            AppLog.Error($"Hover preview click-through subclass failed, hwnd={hwnd}, win32Error={win32Error}.", new InvalidOperationException("SetWindowLongPtr returned null"));
            _hoverVideoWndProc = null;
            return;
        }

        _hoverVideoOriginalWndProc = previous;
        _hoverVideoSubclassedHwnd = hwnd;
        AppLog.Info($"Hover preview click-through: subclassed hwnd={hwnd}, previousWndProc={previous}.");
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void StartHoverPreview(ClipCardViewModel clip, Control border)
    {
        StopHoverPreview();
        _lastHoverPreviewStartUtc = DateTime.UtcNow;
        try
        {
            var player = EnsureHoverPreviewPlayer();
            // Disposing a Media object the instant it's swapped out races LibVLC's own
            // input thread, which can still be tearing it down when a hover-happy user
            // fires several swaps in under a second - that race is what crashed the
            // whole process. Keeping the previous Media alive for one extra swap (i.e.
            // only disposing what's now two generations stale) gives LibVLC enough of a
            // buffer before the object goes away.
            _hoverPreviewMediaPendingDispose?.Dispose();
            _hoverPreviewMediaPendingDispose = _hoverPreviewMedia;
            var media = new Media(_hoverPreviewLibVlc!, new Uri(clip.Path));
            media.AddOption(":no-audio");
            player.Media = media;
            _hoverPreviewMedia = media;
            var played = player.Play();
            _hoverPreviewClip = clip;
            _hoverPreviewBorder = border;
            clip.HoverPreviewPlayer = player;
            _hoverWatchdog.Start();
            EnsureHoverVideoClickThrough(player);
            AppLog.Info($"Clip hover preview started: path={clip.Path}, played={played}, state={player.State}.");
        }
        catch (Exception error)
        {
            AppLog.Error("Clip hover preview failed", error);
            StopHoverPreview();
        }
    }

    private void HoverPreview_OnEndReached(object? sender, EventArgs e)
    {
        // Fires on LibVLC's own thread - hop back before touching the player/UI-bound clip.
        Dispatcher.UIThread.Post(() =>
        {
            if (_hoverPreviewMediaPlayer is not { } player || sender != player) return;
            player.Stop();
            player.Play();
        });
    }

    // Deliberately does not touch _pendingHoverClip/_pendingHoverBorder - those track
    // whichever card the cursor has moved on to (waiting on _hoverPreviewDelay to
    // start it), which is a separate thing from the card whose preview is being torn
    // down here. The watchdog calls this the instant the cursor leaves the old card's
    // bounds, i.e. right as it arrives on the new one - clearing pending here used to
    // erase the record of that new card before its delayed start ever fired, so
    // hovering straight from one card to the next silently started nothing.
    private void StopHoverPreview()
    {
        _hoverWatchdog.Stop();
        _hoverPreviewBorder = null;
        if (_hoverPreviewClip is not null)
        {
            _hoverPreviewClip.HoverPreviewPlayer = null;
            _hoverPreviewClip = null;
        }

        _hoverPreviewMediaPlayer?.Stop();
    }

    private void DisposeHoverPreview()
    {
        StopHoverPreview();
        _hoverPreviewDelay.Stop();
        _pendingHoverClip = null;
        _pendingHoverBorder = null;
        if (_hoverPreviewMediaPlayer is not null)
        {
            _hoverPreviewMediaPlayer.EndReached -= HoverPreview_OnEndReached;
            _hoverPreviewMediaPlayer.Dispose();
            _hoverPreviewMediaPlayer = null;
        }

        _hoverPreviewMedia?.Dispose();
        _hoverPreviewMedia = null;
        _hoverPreviewMediaPendingDispose?.Dispose();
        _hoverPreviewMediaPendingDispose = null;
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

    private void CloseEditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SaveSelectedClipEditState();
        StopEditorPlayback();
        ViewModel?.CloseEditor();
    }

    private async void OpenSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        if (ViewModel.IsSettingsVisible)
        {
            ViewModel.CloseSettings();
            return;
        }

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

    private void ScanMedalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ScanForMedalClips();
    }

    private async void ImportMedalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await EnsureLibraryFolderAsync();
        await ViewModel.ImportSelectedMedalClipsAsync();
    }

    private void AddCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.AddCustomGame();
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
            await ShowMessageAsync("You're up to date", $"EVE {AppUpdateService.CurrentVersion} is the latest version.");
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
                StopEditorPlayback();
                ViewModel.CloseEditor();
                e.Handled = true;
                return;
            }
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
        if (ViewModel?.IsCapturingHotkey != true) return;
        if (_capturedHotkeyKeys.Count == 0) return;

        var hotkey = HotkeyCombo.Normalize(_capturedHotkeyKeys);
        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            ViewModel.SetHotkey(hotkey);
            _globalHotkey?.SetHotkey(hotkey);
        }
        e.Handled = true;
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
            var pauseTime = ViewModel.CurrentTime;
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
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TimelineSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None) return;
        UpdateTimelineFromPointer(e, _timelineDragMode);
    }

    private async void TimelineSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None) return;
        var mode = _timelineDragMode;
        UpdateTimelineFromPointer(e, _timelineDragMode);
        if (mode == TimelineDragMode.Playhead && ViewModel is not null)
        {
            await ApplyTimelineSeekAsync(ViewModel.CurrentTime, _timelineWasPlayingBeforeDrag);
        }
        else if (ViewModel is not null)
        {
            await ApplyTimelineSeekAsync(ViewModel.CurrentTime, _timelineWasPlayingBeforeDrag);
            ViewModel.SaveSelectedClipEditState();
        }

        _timelineDragMode = TimelineDragMode.None;
        _timelineWasPlayingBeforeDrag = false;
        e.Pointer.Capture(null);
        e.Handled = true;
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
        _playback?.SetTrackVolume(track.StreamIndex, track.VolumePercent);
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
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        var safeName = string.Join("_", ViewModel.EditorTitle.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = Path.GetFileNameWithoutExtension(ViewModel.SelectedVideoPath);

        var savedClipsFolder = Path.Combine(
            string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder) ? DefaultLibraryFolder() : ViewModel.Settings.LibraryFolder,
            "Saved Clips");
        Directory.CreateDirectory(savedClipsFolder);
        var suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(savedClipsFolder);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export clip",
            SuggestedFileName = $"{safeName}-trim.mp4",
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
                await ShowMessageAsync("Export complete", outputPath);
            }
        }
        finally
        {
            ViewModel.IsExporting = false;
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string summary)
    {
        var dialog = CreateDialog("Delete clips?", $"{summary}\n\nThis permanently deletes the selected files.", true);
        var result = await dialog.ShowDialog<bool>(this);
        return result;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message, false);
        await dialog.ShowDialog<bool>(this);
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

        var updateButton = new Button { Content = "Update Now", Width = 120, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "primaryButton" } };
        var laterButton = new Button { Content = "Remind Me Later", Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ignoreButton = new Button { Content = "Skip This Version", Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center };

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
        foreach (var note in update.ReleaseNotes.Take(8))
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

        window.Content = new DockPanel
        {
            Children =
            {
                titleBar,
                new ScrollViewer { Content = body }
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

        Dispatcher.UIThread.Post(
            async () =>
            {
                if (cts.IsCancellationRequested) return;
                await StartEditorPlaybackAsync(cts.Token);
            },
            DispatcherPriority.Background);
    }

    private async Task StartEditorPlaybackAsync(CancellationToken cancellationToken)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.SelectedVideoPath)) return;
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) return;

        StopEditorPlayback(cancelQueuedStart: false);

        try
        {
            var playback = new PlaybackSession();
            playback.LoadVideo(ViewModel.SelectedVideoPath);
            _playback = playback;
            AppLog.Info($"Editor open: {ViewModel.SelectedVideoPath}");
            EditorVideoView.MediaPlayer = playback.VideoPlayer;
            var audioTracks = ViewModel.TimelineTracks
                .Where(track => track.IsAudio)
                .Select(track => new AudioPreviewTrack(track.StreamIndex, track.VolumePercent))
                .ToArray();
            if (cancellationToken.IsCancellationRequested) return;

            playback.PlayFrom(ViewModel.CurrentTime);
            StartPlayheadClock(ViewModel.CurrentTime);
            _endedAtTrimBoundary = false;
            ViewModel.IsPlaying = true;
            _playbackTimer.Start();
            _ = LoadEditorAudioAsync(playback, ViewModel.SelectedVideoPath, audioTracks, cancellationToken);
            await Task.Delay(200, cancellationToken);
            if (playback.Duration > TimeSpan.Zero)
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

    private void StopEditorPlayback(bool cancelQueuedStart = true)
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
        var playback = _playback;
        _playback = null;
        EditorVideoView.MediaPlayer = null;
        if (playback is not null)
        {
            playback.Dispose();
        }
        if (ViewModel is not null)
        {
            ViewModel.IsPlaying = false;
        }
    }

    private void SyncPlaybackPosition()
    {
        if (ViewModel is null || _playback is null) return;
        if (_timelineDragMode != TimelineDragMode.None) return;
        if (_playback.Duration > TimeSpan.Zero)
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

    private async Task ApplyTimelineSeekAsync(TimeSpan time, bool resumePlayback)
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
                didResume = await _playback.SeekAsync(time, resumePlayback, seekCts.Token);
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

    private void ApplySavedWindowBounds()
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
