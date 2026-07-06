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

namespace Eve.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer;
    private PlaybackSession? _playback;
    private CancellationTokenSource? _playbackStartCts;
    private TimelineDragMode _timelineDragMode = TimelineDragMode.None;
    private bool _endedAtTrimBoundary;
    private readonly Stopwatch _playheadClock = new();
    private TimeSpan _playheadBaseTime = TimeSpan.Zero;
    private IReplayBuffer? _replayBuffer;
    private GlobalHotkeyService? _globalHotkey;
    private readonly HashSet<string> _capturedHotkeyKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _replayTransitioning;
    private bool _clipSaving;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ApplySavedWindowBounds();
            ViewModel?.UpdateCardLayout(Bounds.Width);
            InitializeReplayServices();
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
            if (_replayBuffer is not null) _replayBuffer.RecordingStopped -= ReplayBuffer_OnRecordingStopped;
            _replayBuffer?.Dispose();
            _playback?.Dispose();
            ViewModel?.Dispose();
        };
        AddHandler(PointerPressedEvent, VolumeSlider_OnPointerPressedAny, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, VolumeSlider_OnPointerReleasedAny, RoutingStrategies.Tunnel, true);
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playbackTimer.Tick += (_, _) => SyncPlaybackPosition();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void InitializeReplayServices()
    {
        if (ViewModel is null || _replayBuffer is not null) return;

        _replayBuffer = ReplayBufferFactory.Create(ViewModel.CreateReplayConfig);
        _replayBuffer.RecordingStopped += ReplayBuffer_OnRecordingStopped;
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.SetHotkey(ViewModel.Settings.SaveReplayHotkey);
        _globalHotkey.Pressed += (_, _) => Dispatcher.UIThread.Post(() => _ = SaveReplayClipAsync());
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
            _ = StartReplayBufferAsync(showErrors: false);
        }
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

        if (_replayBuffer.IsRecording)
        {
            if (_replayTransitioning) return;
            _replayTransitioning = true;
            await _replayBuffer.StopAsync();
            ViewModel.IsReplayRecording = false;
            _replayTransitioning = false;
        }
        else
        {
            await StartReplayBufferAsync(showErrors: true);
        }
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

    private async Task SaveReplayClipAsync()
    {
        if (ViewModel is null) return;
        if (_clipSaving) return;
        InitializeReplayServices();
        if (_replayBuffer is null || !_replayBuffer.IsRecording)
        {
            if (ViewModel.IsReplayRecording) ViewModel.IsReplayRecording = false;
            await ShowMessageAsync("Clip failed", "Replay buffer is not running.");
            return;
        }

        try
        {
            _clipSaving = true;
            await EnsureLibraryFolderAsync();
            var outputFolder = ViewModel.Settings.LibraryFolder;
            AppLog.Info("Replay clip save requested.");
            var outputPath = await Task.Run(() => _replayBuffer.SaveReplayAsync(outputFolder));
            AppLog.Info($"Replay clip saved: {outputPath}");
            await ViewModel.AddOrUpdateLibraryClipAsync(outputPath);
        }
        catch (Exception error)
        {
            AppLog.Error("Replay clip save failed", error);
            await ShowMessageAsync("Clip failed", error.Message);
        }
        finally
        {
            _clipSaving = false;
        }
    }

    private void ReplayBuffer_OnRecordingStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel is not null) ViewModel.IsReplayRecording = false;
        });
    }

    private async Task EnsureLibraryFolderAsync()
    {
        if (ViewModel is null) return;
        if (!string.IsNullOrWhiteSpace(ViewModel.Settings.LibraryFolder) && Directory.Exists(ViewModel.Settings.LibraryFolder)) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select clip folder",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } path) throw new InvalidOperationException("No clip folder selected.");
        await ViewModel.LoadLibraryFolderAsync(path);
    }

    private void TimelineSurface_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateTimelineChrome();
    }

    private async void ClipCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is CheckBox) return;
        if (sender is not Control { DataContext: ClipCardViewModel clip } || ViewModel is null) return;

        e.Handled = true;
        await ViewModel.OpenClipAsync(clip);
        QueueEditorPlayback();
    }

    private void ClipCard_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: ClipCardViewModel clip })
        {
            clip.IsHovered = true;
        }
    }

    private void ClipCard_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: ClipCardViewModel clip })
        {
            clip.IsHovered = false;
        }
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
        ViewModel.OpenSettings();
        await ViewModel.RefreshOpenProcessesAsync();
    }

    private void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseSettings();
    }

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AppLog.OpenFolder();
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
                ViewModel.SeekBySeconds(-1);
                _playback?.Seek(ViewModel.CurrentTime);
                e.Handled = true;
                break;
            case Key.Right:
                _endedAtTrimBoundary = false;
                ViewModel.SeekBySeconds(1);
                _playback?.Seek(ViewModel.CurrentTime);
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

        if (_playback.IsPlaying)
        {
            _playback.Pause();
            SetPlayheadBase(_playback.Position);
            ViewModel.IsPlaying = false;
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
    }

    private void RestartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        ViewModel.RestartPlayback();
        if (_playback is not null)
        {
            if (_playback.IsPlaying)
            {
                _playback.PlayFrom(ViewModel.CurrentTime);
                StartPlayheadClock(ViewModel.CurrentTime);
            }
            else
            {
                _playback.Seek(ViewModel.CurrentTime);
                SetPlayheadBase(ViewModel.CurrentTime);
            }
        }
    }

    private void StepBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        ViewModel.SeekBySeconds(-5);
        _playback?.Seek(ViewModel.CurrentTime);
        SetPlayheadBase(ViewModel.CurrentTime);
    }

    private void StepForwardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = false;
        ViewModel.SeekBySeconds(5);
        _playback?.Seek(ViewModel.CurrentTime);
        SetPlayheadBase(ViewModel.CurrentTime);
    }

    private void EndButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _endedAtTrimBoundary = true;
        ViewModel.CurrentTime = ViewModel.TrimEnd > TimeSpan.Zero ? ViewModel.TrimEnd : ViewModel.Duration;
        _playback?.Seek(ViewModel.CurrentTime);
        SetPlayheadBase(ViewModel.CurrentTime);
    }

    private void TimelineSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.Playhead;
        _endedAtTrimBoundary = false;
        UpdateTimelineFromPointer(e, TimelineDragMode.Playhead);
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimStartHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimStart;
        _endedAtTrimBoundary = false;
        UpdateTimelineFromPointer(e, TimelineDragMode.TrimStart);
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimEndHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimEnd;
        _endedAtTrimBoundary = false;
        UpdateTimelineFromPointer(e, TimelineDragMode.TrimEnd);
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TimelineSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None) return;
        UpdateTimelineFromPointer(e, _timelineDragMode);
    }

    private void TimelineSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None) return;
        var mode = _timelineDragMode;
        UpdateTimelineFromPointer(e, _timelineDragMode);
        if (mode == TimelineDragMode.Playhead && ViewModel is not null)
        {
            _playback?.Seek(ViewModel.CurrentTime);
        }
        else if (ViewModel is not null)
        {
            ViewModel.SaveSelectedClipEditState();
        }

        _timelineDragMode = TimelineDragMode.None;
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

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export clip",
            SuggestedFileName = $"{safeName}-trim.mp4",
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
        _playbackTimer.Stop();
        _playheadClock.Stop();
        _endedAtTrimBoundary = false;
        var playback = _playback;
        _playback = null;
        EditorVideoView.MediaPlayer = null;
        if (playback is not null)
        {
            playback.Stop();
            _ = Task.Run(playback.Dispose);
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
        _playback.SyncAudioStreams();

        if (ViewModel.IsPlaying)
        {
            ViewModel.CurrentTime = SmoothPlaybackPosition();
        }
        else
        {
            ViewModel.CurrentTime = _playback.Position;
            SetPlayheadBase(ViewModel.CurrentTime);
        }
        UpdateTimelineChrome();
        if (ViewModel.TrimEnd > TimeSpan.Zero && ViewModel.CurrentTime >= ViewModel.TrimEnd)
        {
            _playback.Pause();
            _playback.Seek(ViewModel.TrimEnd);
            ViewModel.CurrentTime = ViewModel.TrimEnd;
            SetPlayheadBase(ViewModel.CurrentTime);
            ViewModel.IsPlaying = false;
            _endedAtTrimBoundary = true;
        }
        else if (_playback.IsEnded)
        {
            ViewModel.IsPlaying = false;
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
                _playback?.Seek(ViewModel.CurrentTime);
                ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
                break;
            case TimelineDragMode.TrimEnd:
                ViewModel.TrimEnd = time;
                ViewModel.CurrentTime = ViewModel.TrimEnd;
                _playback?.Seek(ViewModel.CurrentTime);
                ResetPlayheadClockAfterSeek(ViewModel.CurrentTime);
                break;
            case TimelineDragMode.Playhead:
                ViewModel.CurrentTime = time;
                _playback?.Seek(time);
                _playback?.EnsurePlayingIfNeeded(ViewModel.IsPlaying);
                ResetPlayheadClockAfterSeek(time);
                break;
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
