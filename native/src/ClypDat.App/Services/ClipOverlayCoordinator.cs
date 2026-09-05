using Avalonia;

namespace ClypDat.App.Services;

internal enum ClipOverlayKind
{
    Saving,
    Saved,
    Failure,
    Recording,
    GameStarted,
    AutoClip,
    Standalone
}

internal enum ClipOverlayPlacement
{
    TopLeft,
    TopRight,
    CenterLeft,
    CenterRight,
    BottomLeft,
    BottomRight
}

internal static class ClipOverlayPlacementParser
{
    public static ClipOverlayPlacement Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TOP LEFT" => ClipOverlayPlacement.TopLeft,
        "CENTER LEFT" => ClipOverlayPlacement.CenterLeft,
        "CENTER RIGHT" => ClipOverlayPlacement.CenterRight,
        "BOTTOM LEFT" => ClipOverlayPlacement.BottomLeft,
        "BOTTOM RIGHT" => ClipOverlayPlacement.BottomRight,
        _ => ClipOverlayPlacement.TopRight
    };
}

internal sealed record ClipOverlayEvent(
    Guid WorkflowId,
    int Stage,
    DateTime RequestedUtc,
    DateTime OccurredUtc,
    int Priority,
    ClipOverlayKind Kind,
    string Title,
    string? Detail,
    ClipOverlayTarget Target,
    ClipOverlayPlacement Placement,
    bool ExcludeFromCapture,
    bool IsRecovered = false,
    bool ShowVisual = true);

internal sealed record ClipOverlayPresentation(
    long Generation,
    ClipOverlayEvent Event);

internal interface IClipOverlaySurface : IDisposable
{
    void Publish(ClipOverlayPresentation presentation);
    void Dismiss(long generation);
}

internal interface IClipOverlayScheduler : IDisposable
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class ClipOverlayScheduler : IClipOverlayScheduler
{
    private bool _disposed;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        if (_disposed) return EmptyDisposable.Instance;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            callback();
        }, null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    public void Dispose() => _disposed = true;

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}

// Owns notification policy. Producers publish facts; coordinator alone decides
// what can replace the one native surface and when that surface leaves.
internal sealed class ClipOverlayCoordinator : IDisposable
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumEventAge = TimeSpan.FromSeconds(30);
    private const int SoundHistoryLimit = 512;

    private readonly object _gate = new();
    private readonly IClipOverlaySurface _surface;
    private readonly IClipOverlayScheduler _scheduler;
    private readonly Action _playSuccessSound;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<Guid, int> _workflowStages = new();
    private readonly Queue<Guid> _workflowStageOrder = new();
    private readonly HashSet<Guid> _soundedSaves = new();
    private readonly Queue<Guid> _soundOrder = new();
    private ClipOverlayEvent? _visible;
    private IDisposable? _dismissal;
    private long _generation;
    private bool _disposed;

    public ClipOverlayCoordinator(
        IClipOverlaySurface surface,
        IClipOverlayScheduler scheduler,
        Action playSuccessSound,
        Func<DateTime>? utcNow = null)
    {
        _surface = surface;
        _scheduler = scheduler;
        _playSuccessSound = playSuccessSound;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public void Publish(ClipOverlayEvent notification)
    {
        var playSound = false;
        ClipOverlayPresentation? presentation = null;
        lock (_gate)
        {
            if (_disposed) return;

            // Sound belongs to save identity, not visual admission. A lower
            // priority success may lose the surface but must still sound once.
            if (!notification.IsRecovered && notification.Kind == ClipOverlayKind.Saved && _soundedSaves.Add(notification.WorkflowId))
            {
                _soundOrder.Enqueue(notification.WorkflowId);
                while (_soundOrder.Count > SoundHistoryLimit) _soundedSaves.Remove(_soundOrder.Dequeue());
                playSound = true;
            }

            if (notification.IsRecovered || _utcNow() - notification.OccurredUtc > MaximumEventAge)
                goto Finished;
            if (!notification.ShowVisual) goto Finished;

            if (_workflowStages.TryGetValue(notification.WorkflowId, out var lastStage) && notification.Stage <= lastStage)
                goto Finished;

            if (_visible is { } current && current.WorkflowId != notification.WorkflowId)
            {
                if (notification.Priority < current.Priority) goto Finished;
                if (notification.Priority == current.Priority && notification.RequestedUtc <= current.RequestedUtc) goto Finished;
            }

            if (!_workflowStages.ContainsKey(notification.WorkflowId))
            {
                _workflowStageOrder.Enqueue(notification.WorkflowId);
                while (_workflowStageOrder.Count > SoundHistoryLimit)
                    _workflowStages.Remove(_workflowStageOrder.Dequeue());
            }
            _workflowStages[notification.WorkflowId] = notification.Stage;
            _visible = notification;
            var generation = ++_generation;
            _dismissal?.Dispose();
            var dwell = notification.Kind == ClipOverlayKind.GameStarted ? TimeSpan.FromSeconds(5) : Dwell;
            _dismissal = _scheduler.Schedule(dwell, () => Dismiss(generation));
            presentation = new ClipOverlayPresentation(generation, notification);

        Finished:;
        }

        if (playSound) _playSuccessSound();
        if (presentation is not null) _surface.Publish(presentation);
    }

    private void Dismiss(long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _visible = null;
        }
        _surface.Dismiss(generation);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _dismissal?.Dispose();
        }
        _scheduler.Dispose();
        _surface.Dispose();
    }

}

internal static class ClipOverlayLayout
{
    // Flush horizontal placement makes the overlay read as a screen-edge
    // notification instead of a floating dialog. Keep vertical breathing room.
    private const double VerticalInsetDips = 32;

    public static PixelPoint Position(ClipOverlayTarget target, ClipOverlayPlacement placement, int width, int height)
    {
        var verticalInset = (int)Math.Round(VerticalInsetDips * target.Scaling);
        var area = target.WorkArea;
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var x = left ? area.X : area.Right - width;
        var y = placement switch
        {
            ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.CenterRight => area.Y + (area.Height - height) / 2,
            ClipOverlayPlacement.BottomLeft or ClipOverlayPlacement.BottomRight => area.Bottom - verticalInset - height,
            _ => area.Y + verticalInset
        };
        return new PixelPoint(x, y);
    }

    public static PixelPoint AnimatedPosition(ClipOverlayTarget target, ClipOverlayPlacement placement, int width, int height, double progress)
    {
        var final = Position(target, placement, width, height);
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var travel = (int)Math.Round(24 * target.Scaling * (1 - Math.Clamp(progress, 0, 1)));
        var area = target.WorkArea;
        var maximumX = Math.Max(area.X, area.Right - width);
        var x = Math.Clamp(final.X + travel * (left ? 1 : -1), area.X, maximumX);
        return new PixelPoint(x, final.Y);
    }
}
