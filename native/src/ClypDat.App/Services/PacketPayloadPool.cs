namespace ClypDat.App.Services;

// The shared ArrayPool keeps large power-of-two buckets for the life of the
// process. Replay packets are held for minutes, then returned in a burst, so
// using it here retained hundreds of MB after recording stopped. This pool is
// deliberately small, private to the replay ring, and rounds only to 4 KB.
internal sealed class PacketPayloadPool
{
    internal const int QuantumBytes = 4 * 1024;
    internal const int MaximumPooledBufferBytes = 4 * 1024 * 1024;
    internal const int MaximumRetainedBytes = 32 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<int, Stack<byte[]>> _available = new();
    private bool _retentionEnabled = true;
    private int _retainedBytes;

    public int RetainedBytes
    {
        get
        {
            lock (_gate) return _retainedBytes;
        }
    }

    public byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0) return Array.Empty<byte>();

        var length = BucketLength(minimumLength);
        if (length == 0) return GC.AllocateUninitializedArray<byte>(minimumLength);

        lock (_gate)
        {
            if (_available.TryGetValue(length, out var buffers) && buffers.TryPop(out var buffer))
            {
                _retainedBytes -= buffer.Length;
                return buffer;
            }
        }

        return GC.AllocateUninitializedArray<byte>(length);
    }

    public void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        if (BucketLength(buffer.Length) != buffer.Length) return;

        lock (_gate)
        {
            if (!_retentionEnabled) return;
            if (_retainedBytes > MaximumRetainedBytes - buffer.Length) return;
            if (!_available.TryGetValue(buffer.Length, out var buffers))
            {
                buffers = new Stack<byte[]>();
                _available.Add(buffer.Length, buffers);
            }

            buffers.Push(buffer);
            _retainedBytes += buffer.Length;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _available.Clear();
            _retainedBytes = 0;
        }
    }

    public void Activate()
    {
        lock (_gate) _retentionEnabled = true;
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _retentionEnabled = false;
            _available.Clear();
            _retainedBytes = 0;
        }
    }

    internal static int BucketLength(int minimumLength)
    {
        if (minimumLength <= 0 || minimumLength > MaximumPooledBufferBytes) return 0;
        return checked(((minimumLength + QuantumBytes - 1) / QuantumBytes) * QuantumBytes);
    }
}
