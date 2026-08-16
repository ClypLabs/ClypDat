using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public sealed record StorageSaveEstimate(long EstimatedFinalBytes, long TemporaryBytes, long RequiredFreeBytes);

public sealed class StoragePressurePolicy
{
    private readonly string _volumeRole;
    private readonly Queue<(DateTime At, double Ms)> _writes = new();
    private ReplayStorageHealth _health;
    private DateTime? _healthySince;

    public StoragePressurePolicy(string volumeRole = "unknown")
    {
        _volumeRole = volumeRole;
        _health = ReplayStorageHealth.Unknown with { VolumeRole = volumeRole };
    }

    public ReplayStorageHealth Health => _health;

    public ReplayStorageHealth ObserveFreeSpace(long freeBytes, DateTime nowUtc)
    {
        Trim(nowUtc);
        var latencyCritical = _writes.Count(item => item.Ms >= 500) >= 2;
        var latencyWarning = _writes.Count(item => item.Ms >= 100) >= 3;
        var freeCritical = freeBytes >= 0 && freeBytes < 2L * 1024 * 1024 * 1024;
        var freeWarning = freeBytes >= 0 && freeBytes < 10L * 1024 * 1024 * 1024;
        var pressure = freeCritical || latencyCritical ? ReplayStorageState.Critical
            : freeWarning || latencyWarning ? ReplayStorageState.Warning
            : ReplayStorageState.Healthy;

        if (_health.State == ReplayStorageState.Critical && pressure == ReplayStorageState.Healthy && freeBytes < 3L * 1024 * 1024 * 1024)
            pressure = ReplayStorageState.Critical;
        if (_health.State == ReplayStorageState.Warning && pressure == ReplayStorageState.Healthy && freeBytes < 12L * 1024 * 1024 * 1024)
            pressure = ReplayStorageState.Warning;

        if (pressure == ReplayStorageState.Healthy && _health.Reason.Contains("write", StringComparison.OrdinalIgnoreCase))
        {
            _healthySince ??= nowUtc;
            if (_health.State is ReplayStorageState.Warning or ReplayStorageState.Critical
                && nowUtc - _healthySince.Value < TimeSpan.FromSeconds(30))
                pressure = _health.State;
        }
        else _healthySince = null;

        var reason = freeCritical ? "Free space below 2 GB"
            : freeWarning ? "Free space below 10 GB"
            : latencyCritical ? "Two writes exceeded 500 ms in 10 seconds"
            : latencyWarning ? "Three writes exceeded 100 ms in 10 seconds"
            : string.Empty;
        _health = new ReplayStorageHealth(pressure, freeBytes,
            _writes.Count == 0 ? 0 : _writes.Average(item => item.Ms),
            _writes.Count == 0 ? 0 : _writes.Max(item => item.Ms), _volumeRole, reason, nowUtc);
        return _health;
    }

    public ReplayStorageHealth RecordWrite(TimeSpan elapsed, DateTime nowUtc)
    {
        _writes.Enqueue((nowUtc, Math.Max(0, elapsed.TotalMilliseconds)));
        return _health;
    }

    private void Trim(DateTime nowUtc)
    {
        while (_writes.Count > 0 && nowUtc - _writes.Peek().At > TimeSpan.FromSeconds(10)) _writes.Dequeue();
    }
}

public sealed class StorageProtectionService : IDisposable, IStoragePressureObserver
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Role, StoragePressurePolicy Policy)> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private ReplayStorageHealth _health = ReplayStorageHealth.Unknown;

    public ReplayStorageHealth Health { get { lock (_sync) return _health; } }
    public ReplayStorageHealth StorageHealth => Health;
    public bool SavesBlocked => Health.State is ReplayStorageState.Critical or ReplayStorageState.Inaccessible;
    public event EventHandler<ReplayStorageHealth>? HealthChanged;

    public void Start(IEnumerable<(string Path, string Role)> paths)
    {
        lock (_sync)
        {
            foreach (var (path, role) in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(path));
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    if (!_volumes.ContainsKey(root)) _volumes[root] = (role, new StoragePressurePolicy(role));
                }
                catch { }
            }
            _timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    public void RecordWrite(string path, TimeSpan elapsed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return;
            lock (_sync)
            {
                if (_volumes.TryGetValue(root, out var volume)) volume.Policy.RecordWrite(elapsed, DateTime.UtcNow);
            }
        }
        catch { }
    }

    public StorageSaveEstimate EstimateSave(int bitrateMbps, TimeSpan duration)
    {
        var final = (long)Math.Ceiling(Math.Clamp(bitrateMbps, 5, 1000) * 1_000_000d * Math.Max(0, duration.TotalSeconds) / 8d);
        return new StorageSaveEstimate(final, checked(final * 3), checked(final * 3 + 2L * 1024 * 1024 * 1024));
    }

    public bool CanSave(int bitrateMbps, TimeSpan duration, out string reason)
    {
        var estimate = EstimateSave(bitrateMbps, duration);
        lock (_sync)
        {
            if (SavesBlocked)
            {
                reason = $"Storage pressure: {Health.VolumeRole}; {Health.Reason}";
                return false;
            }
            foreach (var root in _volumes)
            {
                try
                {
                    var free = new DriveInfo(root.Key).AvailableFreeSpace;
                    if (free < estimate.RequiredFreeBytes)
                    {
                        reason = $"Save needs {estimate.RequiredFreeBytes} bytes free; {root.Key} has {free}.";
                        return false;
                    }
                }
                catch
                {
                    reason = $"Storage volume {root.Key} is inaccessible.";
                    return false;
                }
            }
        }
        reason = string.Empty;
        return true;
    }

    private void Sample()
    {
        List<ReplayStorageHealth> samples = new();
        lock (_sync)
        {
            foreach (var (root, volume) in _volumes)
            {
                try { samples.Add(volume.Policy.ObserveFreeSpace(new DriveInfo(root).AvailableFreeSpace, DateTime.UtcNow)); }
                catch { samples.Add(new ReplayStorageHealth(ReplayStorageState.Inaccessible, -1, 0, 0, volume.Role, "Volume inaccessible", DateTime.UtcNow)); }
            }
            _health = samples.OrderByDescending(item => item.State).ThenBy(item => item.FreeBytes).FirstOrDefault() ?? ReplayStorageHealth.Unknown;
        }
        HealthChanged?.Invoke(this, Health);
    }

    public void Dispose()
    {
        lock (_sync) { _timer?.Dispose(); _timer = null; }
    }
}
