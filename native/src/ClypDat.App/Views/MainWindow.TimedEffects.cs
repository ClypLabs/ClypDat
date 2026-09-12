using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    // Text and blur draw live in the overlay window (MainWindow.Spotify.cs), so
    // playback always runs on the original file at full quality. This file only
    // wires the pieces that need the window: seeking, keys and the video-track
    // menu.
    private TimedEffectLayer? _timedEffectLayer;
    private MainWindowViewModel? _timedEffectModel;

    private void InitializeTimedEffects()
    {
        DataContextChanged += (_, _) =>
        {
            if (_timedEffectModel is not null)
            {
                _timedEffectModel.TimedEffectSeekRequested -= OnTimedEffectSeekRequested;
                _timedEffectModel.TimedEffectAddRequested -= OnTimedEffectAddRequested;
            }
            _timedEffectModel = ViewModel;
            if (_timedEffectModel is not null)
            {
                _timedEffectModel.TimedEffectSeekRequested += OnTimedEffectSeekRequested;
                _timedEffectModel.TimedEffectAddRequested += OnTimedEffectAddRequested;
            }
        };
    }

    private void OnTimedEffectSeekRequested(object? sender, TimeSpan time)
    {
        if (ViewModel is null) return;
        _ = ApplyTimelineSeekAsync(time, ViewModel.IsPlaying);
        UpdateTimelineChrome();
    }

    private void PositionTimedEffectTrack(double videoLaneHeight)
    {
        if (ViewModel is null) return;
        var top = 0d;
        foreach (var track in ViewModel.TimelineTracks)
        {
            if (track.IsVideo) break;
            top += track.LaneHeight + track.LaneMargin.Bottom;
        }
        TimedEffectTrack.Margin = new Thickness(0, top, 0, 0);
        TimedEffectTrack.Height = Math.Max(0, videoLaneHeight);
    }

    /// <summary>Delete removes the selected text or blur; Ctrl+D duplicates it.</summary>
    private bool HandleTimedEffectKey(KeyEventArgs e)
    {
        if (ViewModel is not { IsEditorVisible: true } model || model.SelectedTimedEffectId is not { } id) return false;
        try
        {
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) model.RemoveTimedEffect(id);
            else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control) model.DuplicateTimedEffect(id);
            else return false;
        }
        catch (Exception error) { AppLog.Error("Timed effect shortcut failed", error); }
        e.Handled = true;
        return true;
    }

    /// <summary>Right-click on the Video track offers to add text or blur at
    /// the clicked time. Other lanes keep their normal click behaviour.</summary>
    private bool TryOpenVideoLaneMenu(PointerPressedEventArgs e)
    {
        if (ViewModel is not { } model || !e.GetCurrentPoint(TimelineSurface).Properties.IsRightButtonPressed) return false;
        var point = e.GetPosition(TimedEffectTrack);
        if (point.Y < 0 || point.Y > TimedEffectTrack.Bounds.Height || TimelineSurface.Bounds.Width <= 0) return false;
        var seconds = Math.Clamp(e.GetPosition(TimelineSurface).X / TimelineSurface.Bounds.Width, 0, 1) * model.Duration.TotalSeconds;
        MenuItem Item(string header, bool blur)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                try { AddTimedEffect(blur, seconds); }
                catch (Exception error) { AppLog.Error("Add timed effect failed", error); }
            };
            return item;
        }
        new ContextMenu { ItemsSource = new[] { Item("Add text here", false), Item("Add blur here", true) } }.Open(TimelineSurface);
        e.Handled = true;
        return true;
    }

    /// <summary>Pauses, adds at <paramref name="seconds"/>, and parks the
    /// playhead on the new clip so it is on screen to be dragged into place.</summary>
    private void AddTimedEffect(bool blur, double seconds)
    {
        if (ViewModel is not { } model) return;
        PauseEditorPlayback();
        var effect = model.AddTimedEffect(blur, seconds);
        if (!blur) model.RequestTimedEffectCaptionFocus();
        if (Math.Abs(model.CurrentTime.TotalSeconds - effect.Start) > .001)
            _ = ApplyTimelineSeekAsync(TimeSpan.FromSeconds(effect.Start), false);
    }

    private void OnTimedEffectAddRequested(object? sender, bool blur)
    {
        if (ViewModel is null) return;
        AddTimedEffect(blur, ViewModel.CurrentTime.TotalSeconds);
    }
}
