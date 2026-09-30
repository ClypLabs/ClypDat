using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public sealed record StorageSaveEstimate(long EstimatedFinalBytes, long TemporaryBytes, long RequiredFreeBytes);

public sealed class StoragePressurePolicy
{
    private readonly string _volumeRole;
    private readonly Queue<(DateTime At, double Ms)> _writes = new();
    private ReplayStorageHealth _health;
    private DateTime? _healthySince;
    private ReplayStorageState _capacityState, _latencyState;
    private string _latencyReason = string.Empty;

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
        _capacityState = freeCritical || (_capacityState == ReplayStorageState.Critical && freeBytes < 3L * 1024 * 1024 * 1024)
            ? ReplayStorageState.Critical
            : freeWarning || (_capacityState != ReplayStorageState.Healthy && freeBytes < 12L * 1024 * 1024 * 1024)
                ? ReplayStorageState.Warning : ReplayStorageState.Healthy;
        var latency = latencyCritical ? ReplayStorageState.Critical : latencyWarning ? ReplayStorageState.Warning : ReplayStorageState.Healthy;
        if (latency == ReplayStorageState.Healthy && _latencyState != ReplayStorageState.Healthy)
        {
            _healthySince ??= nowUtc;
            if (nowUtc - _healthySince.Value >= TimeSpan.FromSeconds(30))
            { _latencyState = ReplayStorageState.Healthy; _latencyReason = string.Empty; }
        }
        else if (latency != ReplayStorageState.Healthy)
        {
            _healthySince = null;
            _latencyState = latency;
            _latencyReason = latencyCritical ? "Two writes exceeded 500 ms in 10 seconds" : "Three writes exceeded 100 ms in 10 seconds";
        }
        var pressure = (ReplayStorageState)Math.Max((int)_capacityState, (int)_latencyState);
        var capacityReason = _capacityState == ReplayStorageState.Critical ? "Free space below 3 GB recovery reserve"
            : _capacityState == ReplayStorageState.Warning ? "Free space below 12 GB recovery reserve" : string.Empty;
        var reason = string.Join("; ", new[] { capacityReason, _latencyReason }.Where(value => value.Length > 0));
        _health = new ReplayStorageHealth(pressure, freeBytes,
            _writes.Count == 0 ? 0 : _writes.Average(item => item.Ms),
            _writes.Count == 0 ? 0 : _writes.Max(item => item.Ms), _volumeRole, reason, nowUtc)
        {
            Cause = (_capacityState == ReplayStorageState.Healthy ? ReplayStoragePressureCause.None : ReplayStoragePressureCause.Capacity)
                | (_latencyState == ReplayStorageState.Healthy ? ReplayStoragePressureCause.None : ReplayStoragePressureCause.WriteLatency)
        };
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
    private readonly Func<string, long> _freeSpace;
    private readonly Action<string> _checkDestination;
    private double _lastSaveDurationMs;

    public StorageProtectionService() : this(GetAvailableFreeBytes, CheckDestination) { }
    internal StorageProtectionService(Func<string, long> freeSpace, Action<string> checkDestination)
    { _freeSpace = freeSpace; _checkDestination = checkDestination; }
    internal IReadOnlyList<string> MonitoredRoots { get { lock (_sync) return _volumes.Keys.ToArray(); } }

    public ReplayStorageHealth Health { get { lock (_sync) return _health; } }
    public ReplayStorageHealth StorageHealth => Health;
    public bool SavesBlocked => Health.State is ReplayStorageState.Critical or ReplayStorageState.Inaccessible;
    public event EventHandler<ReplayStorageHealth>? HealthChanged;

    public void Start(IEnumerable<(string Path, string Role)> paths)
    {
        lock (_sync)
        {
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, role) in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(path));
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    active.Add(root);
                    roles[root] = roles.TryGetValue(root, out var previous) ? previous + ", " + role : role;
                    if (!_volumes.ContainsKey(root)) _volumes[root] = (role, new StoragePressurePolicy(role));
                }
                catch { }
            }
            foreach (var obsolete in _volumes.Keys.Where(root => !active.Contains(root)).ToArray()) _volumes.Remove(obsolete);
            foreach (var (root, role) in roles) _volumes[root] = (role, _volumes[root].Policy);
            _health = ReplayStorageHealth.Unknown;
            _timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    public void RecordSaveDuration(TimeSpan elapsed)
    { lock (_sync) _lastSaveDurationMs = Math.Max(0, elapsed.TotalMilliseconds); }

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
        var final = (long)Math.Ceiling(Math.Clamp(bitrateMbps, 5, 100) * 1_000_000d * Math.Max(0, duration.TotalSeconds) / 8d);
        return new StorageSaveEstimate(final, checked(final * 3), checked(final * 3 + 2L * 1024 * 1024 * 1024));
    }

    public bool CanSave(int bitrateMbps, TimeSpan duration, out string reason)
        => CanSave(MonitoredRoots, bitrateMbps, duration, out reason);

    public bool CanSave(IEnumerable<string> paths, int bitrateMbps, TimeSpan duration, out string reason)
    {
        var estimate = EstimateSave(bitrateMbps, duration);
        // Queried outside the lock: a network share can take seconds to answer.
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _checkDestination(path);
                var free = _freeSpace(path);
                if (free < estimate.RequiredFreeBytes)
                {
                    reason = $"Save needs {estimate.RequiredFreeBytes} bytes free; {path} has {free} bytes available.";
                    return false;
                }
                lock (_sync)
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(path));
                    if (root is not null && _volumes.TryGetValue(root, out var volume))
                    {
                        var health = volume.Policy.ObserveFreeSpace(free, DateTime.UtcNow);
                        if (health.State == ReplayStorageState.Critical && health.Cause.HasFlag(ReplayStoragePressureCause.WriteLatency))
                        { reason = $"Storage pressure at {path}: {health.Reason}"; return false; }
                    }
                }
            }
            catch (Exception error)
            {
                reason = $"Storage location {path} is inaccessible: {error.Message}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private int _sampling;

    private void Sample()
    {
        // The timer fires every 5s whether or not the last sample has returned,
        // and a stalled share must not stack up callbacks behind it.
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            string[] roots;
            lock (_sync) roots = _volumes.Keys.ToArray();
            var free = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                try { free[root] = _freeSpace(root); }
                catch { free[root] = null; }
            }

            List<ReplayStorageHealth> samples = new();
            lock (_sync)
            {
                foreach (var (root, volume) in _volumes)
                {
                    if (!free.TryGetValue(root, out var bytes)) continue; // Config changed while an old share was answering.
                    if (bytes is { } available)
                        samples.Add(volume.Policy.ObserveFreeSpace(available, DateTime.UtcNow) with { Location = root, VolumeRole = volume.Role });
                    else
                        samples.Add(new ReplayStorageHealth(ReplayStorageState.Inaccessible, -1, 0, 0, volume.Role, $"Volume {root} inaccessible", DateTime.UtcNow)
                        { Cause = ReplayStoragePressureCause.Inaccessible, Location = root });
                }
                _health = (samples.OrderByDescending(item => item.State).ThenBy(item => item.FreeBytes).FirstOrDefault() ?? ReplayStorageHealth.Unknown)
                    with { LastSaveDurationMs = _lastSaveDurationMs };
            }
            HealthChanged?.Invoke(this, Health);
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    // DriveInfo only understands drive letters: handed a UNC root such as
    // \\nas\clips it throws, which used to mark the volume inaccessible and block
    // every save for a library on a network share. GetDiskFreeSpaceEx takes any
    // directory, and reports the space available to this user (quotas included).
    internal static long GetAvailableFreeBytes(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = root.EndsWith('\\') ? root : root + "\\";
            if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _))
                throw new IOException($"Could not read free space for {root}.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static void CheckDestination(string path)
    {
        Directory.CreateDirectory(path);
        using var probe = new FileStream(Path.Combine(path, ".clypdat-storage-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal static bool IsUncRoot(string root) =>
        root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    public void Dispose()
    {
        lock (_sync) { _timer?.Dispose(); _timer = null; }
    }
}
