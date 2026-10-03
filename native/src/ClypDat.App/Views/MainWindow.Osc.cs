using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Views;

public sealed partial class MainWindow
{
    private OscUdpListener? _oscListener;
    private OscCaptureSnapshot? _oscCaptureSnapshot;
    private long _oscCaptureGeneration;
    private bool _applyingOscDuration;
    private bool _oscDurationNeedsReconcile;

    private void InitializeOscControls()
    {
        _oscListener = new OscUdpListener(action => Dispatcher.UIThread.Post(action), HandleOscPacket,
            status => { if (ViewModel is { } vm) vm.OscListenerStatus = status; }, CaptureOscReceipt);
        ViewModel!.RecordingSettingsSaved += ApplyOscListenerSettings;
        ApplyOscListenerSettings();
        RefreshOscCaptureSnapshot();
    }

    private void ApplyOscListenerSettings()
    {
        if (ViewModel is not { } vm || IsQuitting || AllowRealClose) return;
        _oscListener?.Configure(vm.OscEnabled, vm.OscPort);
    }

    private void StopOscControls()
    {
        _oscListener?.Dispose();
        _oscListener = null;
        Volatile.Write(ref _oscCaptureSnapshot, null);
        if (ViewModel is { } vm) vm.RecordingSettingsSaved -= ApplyOscListenerSettings;
    }

    private object? CaptureOscReceipt()
    {
        var snapshot = Volatile.Read(ref _oscCaptureSnapshot);
        return snapshot is not null && (_clipSaveLock.CurrentCount == 0 || ShutdownGuard.IsBusy)
            ? snapshot with { Refusal = "another operation is busy" } : snapshot;
    }

    private OscCaptureSnapshot? CreateOscCaptureSnapshot(bool ownsSave = false)
    {
        if (ViewModel is not { } vm || _replayBuffer is not { } buffer || _activeReplayConfigSnapshot is not { } config) return null;
        var health = (buffer as IReplayCaptureDiagnostics)?.GetHealthSnapshot();
        var desired = vm.CreateReplayConfig();
        var refusal = OscClipPolicy.Refusal(IsQuitting || AllowRealClose,
            (!ownsSave && (_clipSaveLock.CurrentCount == 0 || ShutdownGuard.IsBusy)) || health?.SaveInProgress == true,
            _replayTransitioning || _replayRestartDebounceTimer is not null || RuntimeSettingsDiffer(config, desired) ||
                !string.Equals(ReplayTargetIdentity(config), ReplayTargetIdentity(desired), StringComparison.Ordinal),
            vm.IsReplayArming || health?.State is ReplayCaptureState.Starting or ReplayCaptureState.Unknown,
            vm.IsReplaySuspended || buffer is IReplayCaptureWorkerEvents { IsCaptureSuspended: true },
            vm.Settings.ReplayBufferEnabled && buffer.IsRecording && health is { State: ReplayCaptureState.Healthy or ReplayCaptureState.Degraded, CapturePaused: false, OutputFrameRate: > 0 });
        return new(buffer, config, _oscCaptureGeneration, (buffer as CaptureWorkerProxy)?.CaptureGeneration ?? 0,
            vm.EffectiveClipGameName(config.GameDisplayName, config.CaptureSource), refusal);
    }

    private void RefreshOscCaptureSnapshot() => Volatile.Write(ref _oscCaptureSnapshot, CreateOscCaptureSnapshot());

    private void InvalidateOscCapture()
    {
        _oscCaptureGeneration++;
        RefreshOscCaptureSnapshot();
    }

    private void HandleOscPacket(OscReceivedPacket packet)
    {
        if (IsQuitting || AllowRealClose) return;
        packet.Dispatch(seconds =>
        {
            _applyingOscDuration = true;
            try
            {
                var changed = ViewModel?.ApplyOscReplayDuration(seconds) == true;
                _oscDurationNeedsReconcile |= changed;
                ReconcileOscDuration();
                AppLog.Info($"[OSC] Replay duration {(changed ? "set to" : "unchanged at")} {seconds}s.");
                RefreshOscCaptureSnapshot();
            }
            finally { _applyingOscDuration = false; }
        }, received =>
        {
            if (received.ReceiptContext is not OscCaptureSnapshot snapshot)
            {
                AppLog.Info("[OSC] Clip rejected: capture unavailable at reception.");
                return;
            }
            var request = new OscClipRequest(snapshot, received.ReceivedUtc, received.ListenerLifetime);
            if (OscClipPolicy.Validate(request, CreateOscCaptureSnapshot()) is { } refusal)
            {
                AppLog.Info($"[OSC] Clip rejected: {refusal}.");
                return;
            }
            _ = SaveOscClipAsync(request);
        });
    }

    private async Task SaveOscClipAsync(OscClipRequest request)
    {
        AppLog.Info($"[OSC] Clip requested: receivedUtc={request.ReceivedUtc:O}.");
        var saved = await SaveReplayClipAsync(clipWindow: request.Window, oscRequest: request);
        AppLog.Info(saved ? "[OSC] Clip saved." : "[OSC] Clip failed or rejected; see preceding save log.");
    }

    private void ReconcileOscDuration()
    {
        // A command can arrive while StartAsync still owns an older settings
        // snapshot. SaveSettings has no active config to compare at that point;
        // reconcile again after startup so the new length actually reaches it.
        if (!_oscDurationNeedsReconcile || _replayTransitioning || ViewModel is not { } vm ||
            _activeReplayConfigSnapshot is not { } active || _replayBuffer is not { IsRecording: true }) return;
        _oscDurationNeedsReconcile = false;
        if (_replayRestartDebounceTimer is null && RuntimeSettingsDiffer(active, vm.CreateReplayConfig()))
            ScheduleReplayRestart(showErrors: false);
    }
}
