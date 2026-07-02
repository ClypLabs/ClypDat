using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using Eve.App.Services;
using Eve.App.ViewModels;

namespace Eve.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _smoothPlaybackClock = new();
    private PlaybackSession? _playback;
    private CancellationTokenSource? _playbackStartCts;
    private TimelineDragMode _timelineDragMode = TimelineDragMode.None;
    private TimeSpan _smoothPlaybackBase = TimeSpan.Zero;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ViewModel?.UpdateCardLayout(Bounds.Width);
        KeyDown += MainWindow_OnKeyDown;
        Closing += (_, _) => ViewModel?.SaveSettings();
        Closed += (_, _) => _playback?.Dispose();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playbackTimer.Tick += (_, _) => SyncPlaybackPosition();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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

    private async void ClipCard_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: ClipCardViewModel clip })
        {
            clip.IsHovered = true;
            await clip.StartPreviewAsync();
        }
    }

    private void ClipCard_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: ClipCardViewModel clip })
        {
            clip.IsHovered = false;
            clip.StopPreview();
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
        StopEditorPlayback();
        ViewModel?.CloseEditor();
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
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
                ViewModel.SeekBySeconds(-1);
                _playback?.Seek(ViewModel.CurrentTime);
                SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
                e.Handled = true;
                break;
            case Key.Right:
                ViewModel.SeekBySeconds(1);
                _playback?.Seek(ViewModel.CurrentTime);
                SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
                e.Handled = true;
                break;
            case Key.Space:
                PlayPauseButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
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

        if (_playback.IsPlaying)
        {
            SyncSmoothPlaybackClock(_playback.Position, false);
            _playback.Pause();
            ViewModel.IsPlaying = false;
            return;
        }

        SyncSmoothPlaybackClock(ViewModel.CurrentTime, true);
        _playback.Play();
        ViewModel.IsPlaying = true;
    }

    private void RestartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.RestartPlayback();
        _playback?.Seek(ViewModel.CurrentTime);
        SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
    }

    private void StepBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SeekBySeconds(-5);
        _playback?.Seek(ViewModel.CurrentTime);
        SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
    }

    private void StepForwardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SeekBySeconds(5);
        _playback?.Seek(ViewModel.CurrentTime);
        SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
    }

    private void EndButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.CurrentTime = ViewModel.TrimEnd > TimeSpan.Zero ? ViewModel.TrimEnd : ViewModel.Duration;
        _playback?.Seek(ViewModel.CurrentTime);
        SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
    }

    private void TimelineSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.Playhead;
        UpdateTimelineFromPointer(e, TimelineDragMode.Playhead);
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimStartHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimStart;
        e.Pointer.Capture(TimelineSurface);
        e.Handled = true;
    }

    private void TrimEndHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Duration <= TimeSpan.Zero) return;
        _timelineDragMode = TimelineDragMode.TrimEnd;
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
        UpdateTimelineFromPointer(e, _timelineDragMode);
        if (_timelineDragMode == TimelineDragMode.Playhead && ViewModel is not null)
        {
            _playback?.Seek(ViewModel.CurrentTime);
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
        }
    }

    private void TrackVolume_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider { DataContext: TrackLaneViewModel track })
        {
            track.ShowVolumePercent = false;
        }
    }

    private void TrackVolume_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || sender is not Slider { DataContext: TrackLaneViewModel track }) return;
        _playback?.SetTrackVolume(track.StreamIndex, track.VolumePercent);
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

        ViewModel.IsExporting = true;
        try
        {
            var args = ViewModel.BuildExportArguments(outputPath);
            var result = await RunProcessAsync("ffmpeg", args);
            if (result.ExitCode != 0)
            {
                await ShowMessageAsync("Export failed", string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg failed." : result.Error);
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
            _playback = new PlaybackSession();
            EditorVideoView.MediaPlayer = _playback.VideoPlayer;
            var audioStreams = ViewModel.TimelineTracks
                .Where(track => track.IsAudio)
                .Select(track => track.StreamIndex)
                .ToArray();
            _playback.Load(ViewModel.SelectedVideoPath, audioStreams);
            if (cancellationToken.IsCancellationRequested) return;
            foreach (var track in ViewModel.TimelineTracks.Where(track => track.IsAudio))
            {
                _playback.SetTrackVolume(track.StreamIndex, track.VolumePercent);
            }

            _playback.Play();
            SyncSmoothPlaybackClock(ViewModel.CurrentTime, true);
            ViewModel.IsPlaying = true;
            _playbackTimer.Start();
            await Task.Delay(200, cancellationToken);
            if (_playback.Duration > TimeSpan.Zero)
            {
                ViewModel.SetDuration(_playback.Duration);
            }
            UpdateTimelineChrome();
        }
        catch (Exception error)
        {
            StopEditorPlayback();
            await ShowMessageAsync("Playback unavailable", error.Message);
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
        _smoothPlaybackClock.Reset();
        _smoothPlaybackBase = TimeSpan.Zero;
        var playback = _playback;
        _playback = null;
        EditorVideoView.MediaPlayer = null;
        if (playback is not null)
        {
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

        if (_playback.IsPlaying)
        {
            var smoothTime = _smoothPlaybackBase + _smoothPlaybackClock.Elapsed;
            var vlcTime = _playback.Position;
            if (Math.Abs((vlcTime - smoothTime).TotalMilliseconds) > 350)
            {
                SyncSmoothPlaybackClock(vlcTime, true);
                smoothTime = vlcTime;
            }
            ViewModel.CurrentTime = smoothTime;
        }
        else
        {
            ViewModel.CurrentTime = _playback.Position;
            SyncSmoothPlaybackClock(ViewModel.CurrentTime, false);
        }
        UpdateTimelineChrome();
        if (ViewModel.TrimEnd > TimeSpan.Zero && ViewModel.CurrentTime >= ViewModel.TrimEnd)
        {
            _playback.Pause();
            _playback.Seek(ViewModel.TrimEnd);
            SyncSmoothPlaybackClock(ViewModel.TrimEnd, false);
            ViewModel.IsPlaying = false;
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
                SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
                break;
            case TimelineDragMode.TrimEnd:
                ViewModel.TrimEnd = time;
                ViewModel.CurrentTime = ViewModel.TrimEnd;
                _playback?.Seek(ViewModel.CurrentTime);
                SyncSmoothPlaybackClock(ViewModel.CurrentTime, _playback?.IsPlaying == true);
                break;
            case TimelineDragMode.Playhead:
                ViewModel.CurrentTime = time;
                _playback?.Seek(time);
                SyncSmoothPlaybackClock(time, _playback?.IsPlaying == true);
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

    private void SyncSmoothPlaybackClock(TimeSpan position, bool running)
    {
        _smoothPlaybackBase = position;
        _smoothPlaybackClock.Restart();
        if (!running)
        {
            _smoothPlaybackClock.Stop();
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

    private static bool IsTypingInTextInput(object? source)
    {
        return source is TextBox;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
