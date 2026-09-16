using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;

namespace ClypDat.App.Services;

// Only the writer worker touches WAV state. The capture callback owns no disk
// handles and holds the queue gate only while copying/admitting a packet.
internal sealed class AudioCaptureSession : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly WaveFormat _format;
    private Stream _stream;
    private WaveFileWriter _writer;
    private readonly object _queueGate = new();
    private readonly BlockingCollection<Work> _queue = new();
    private readonly TaskCompletionSource _writerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _waitTimeout;
    private readonly string? _sourcePath;
    private readonly bool _memoryBacked;
    private readonly long _queueLimit;
    private readonly Func<string, Stream> _snapshotOutput;
    private long _queuedBytes, _peakQueuedBytes, _acceptedBytes, _lostBytes;
    private long _maxCallbackTicks;
    private int _died, _disposeStarted, _rejectPackets;
    private bool _closed, _firstSampleSeen;
    private long _firstSampleTicks;
    private Exception? _writerError;
    private Snapshot? _finalSnapshot;
    private Task? _stopTask;

    private sealed record Work(byte[]? Buffer, int Count, DateTime ArrivalUtc, DateTime? PacketUtc, Action? Control);
    private sealed record Snapshot(string? SourcePath, byte[]? Memory, long Offset, long Count, DateTime LastSampleUtc);

    private AudioCaptureSession(IWaveIn capture, Stream stream, WaveFileWriter writer, string title, string? sourcePath, TimeSpan? waitTimeout, Func<string, Stream>? snapshotOutput)
    {
        _capture = capture;
        _format = capture.WaveFormat;
        _stream = stream;
        _writer = writer;
        Title = title;
        _sourcePath = sourcePath ?? (stream as FileStream)?.Name;
        _memoryBacked = stream is MemoryStream;
        _queueLimit = (long)_format.AverageBytesPerSecond * 10;
        _waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(5);
        _snapshotOutput = snapshotOutput ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }

    public string Title { get; }
    public DateTime? FirstSampleUtc
    {
        get { var ticks = Interlocked.Read(ref _firstSampleTicks); return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc); }
        private set => Interlocked.Exchange(ref _firstSampleTicks, value?.Ticks ?? 0);
    }
    public bool Died { get => Volatile.Read(ref _died) != 0; internal set => Volatile.Write(ref _died, value ? 1 : 0); }
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    internal bool HasAcceptedAudio => Interlocked.Read(ref _acceptedBytes) > 0;
    internal long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    internal long LostBytes => Interlocked.Read(ref _lostBytes);
    internal Task WriterCompletion => _writerExited.Task;
    public int AverageBytesPerSecond => _format.AverageBytesPerSecond;
    public bool IsMemoryBacked => _memoryBacked;
    internal static bool UsesBackgroundIo(AudioSnapshotPurpose purpose) => purpose == AudioSnapshotPurpose.BackgroundArchive;

    public static AudioCaptureSession Start(IWaveIn capture, string path, string title) =>
        StartOn(capture, new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), title);

    public static AudioCaptureSession StartInMemory(IWaveIn capture, string title, int capacityHintBytes = 0) =>
        StartOn(capture, capacityHintBytes > 0 ? new MemoryStream(capacityHintBytes) : new MemoryStream(), title);

    // Stream injection keeps disk-stall/failure tests on the real capture path.
    internal static AudioCaptureSession StartOn(IWaveIn capture, Stream stream, string title, string? sourcePath = null, TimeSpan? waitTimeout = null, Func<string, Stream>? snapshotOutput = null)
    {
        WaveFileWriter writer;
        try { writer = new WaveFileWriter(stream, capture.WaveFormat); }
        catch { stream.Dispose(); throw; }
        var session = new AudioCaptureSession(capture, stream, writer, title, sourcePath, waitTimeout, snapshotOutput);
        capture.DataAvailable += session.Capture_OnDataAvailable;
        capture.RecordingStopped += session.Capture_OnRecordingStopped;
        new Thread(session.WriterLoop) { IsBackground = true, Name = $"ClypDat audio writer {title}" }.Start();
        try { capture.StartRecording(); }
        catch
        {
            session.CloseQueue();
            session._captureStopped.TrySetResult();
            session.Dispose();
            throw;
        }
        return session;
    }

    private void Capture_OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var started = Stopwatch.GetTimestamp();
        var arrival = MonotonicClock.UtcNow;
        if (e.BytesRecorded <= 0) return;
        try
        {
            lock (_queueGate)
            {
                if (_closed || Volatile.Read(ref _rejectPackets) != 0)
                {
                    Interlocked.Add(ref _lostBytes, e.BytesRecorded);
                    return;
                }
                // Include the in-flight write in this limit: blocked I/O still
                // owns its buffer. Never wait for the worker to free capacity.
                if (_queuedBytes + e.BytesRecorded > _queueLimit)
                {
                    Interlocked.Add(ref _lostBytes, e.BytesRecorded);
                    Volatile.Write(ref _rejectPackets, 1);
                    Fail($"queue overflow; rejectedMs={BytesToMilliseconds(e.BytesRecorded):0.###}, queuedMs={BytesToMilliseconds(_queuedBytes):0.###}");
                    return;
                }
                var owned = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
                e.Buffer.AsSpan(0, e.BytesRecorded).CopyTo(owned);
                Interlocked.Add(ref _queuedBytes, e.BytesRecorded);
                _peakQueuedBytes = Math.Max(_peakQueuedBytes, _queuedBytes);
                Interlocked.Add(ref _acceptedBytes, e.BytesRecorded);
                _queue.Add(new Work(owned, e.BytesRecorded, arrival, (e as TimestampedWaveInEventArgs)?.PacketStartUtc, null));
            }
        }
        catch (Exception error) { Volatile.Write(ref _rejectPackets, 1); Fail("packet enqueue failed", error); }
        finally { UpdateMaximum(ref _maxCallbackTicks, Stopwatch.GetTimestamp() - started); }
    }

    private void Capture_OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Fail("capture stopped unexpectedly", e.Exception);
        // Process loopback emits its held-back packet before this event.
        CloseQueue();
        _captureStopped.TrySetResult();
    }

    private void CloseQueue()
    {
        lock (_queueGate)
        {
            if (_closed) return;
            _closed = true;
            _queue.CompleteAdding();
        }
    }

    private void Fail(string reason, Exception? error = null)
    {
        Died = true; // Existing route recovery replaces failed sessions.
        AppLog.Error($"Audio capture failed: {Title}, {reason}, lostMs={BytesToMilliseconds(Interlocked.Read(ref _lostBytes)):0.###}.", error);
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long old;
        do { old = Volatile.Read(ref target); if (value <= old) return; }
        while (Interlocked.CompareExchange(ref target, value, old) != old);
    }

    private void WriterLoop()
    {
        var lastFlush = Stopwatch.GetTimestamp();
        var nextDiagnostic = lastFlush + Stopwatch.Frequency * 60;
        long maxWriteTicks = 0, stalls = 0;
        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var work, 100))
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        if (work.Control is not null) work.Control();
                        else if (_writerError is null)
                        {
                            WaveInEventArgs packet = work.PacketUtc is { } timestamp
                                ? new TimestampedWaveInEventArgs(work.Buffer!, work.Count, timestamp)
                                : new WaveInEventArgs(work.Buffer!, work.Count);
                            WritePacket(packet, work.ArrivalUtc);
                        }
                        else Interlocked.Add(ref _lostBytes, work.Count);
                    }
                    catch (Exception error)
                    {
                        Interlocked.Add(ref _lostBytes, work.Count);
                        _writerError = error; Volatile.Write(ref _rejectPackets, 1);
                        Fail("writer failed; current packet loss is an upper bound", error);
                    }
                    finally
                    {
                        if (work.Buffer is not null)
                        {
                            ArrayPool<byte>.Shared.Return(work.Buffer);
                            Interlocked.Add(ref _queuedBytes, -work.Count);
                        }
                        var elapsed = Stopwatch.GetTimestamp() - started;
                        maxWriteTicks = Math.Max(maxWriteTicks, elapsed);
                        if (elapsed > Stopwatch.Frequency / 10) stalls++;
                    }
                }
                if (_writerError is null && Stopwatch.GetElapsedTime(lastFlush).TotalSeconds >= 1)
                {
                    var started = Stopwatch.GetTimestamp();
                    try { _writer.Flush(); }
                    catch (Exception error) { _writerError = error; Volatile.Write(ref _rejectPackets, 1); Fail("writer flush failed", error); }
                    var elapsed = Stopwatch.GetTimestamp() - started;
                    maxWriteTicks = Math.Max(maxWriteTicks, elapsed);
                    if (elapsed > Stopwatch.Frequency / 10) stalls++;
                    lastFlush = Stopwatch.GetTimestamp();
                }
                if (Stopwatch.GetTimestamp() >= nextDiagnostic)
                {
                    LogWriterDiagnostic(maxWriteTicks, stalls);
                    maxWriteTicks = stalls = 0;
                    nextDiagnostic = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
                }
            }
        }
        finally
        {
            // Disposal can block too. Only this worker may dispose its stream;
            // callers timing out leave these resources owned until it exits.
            try
            {
                if (_writerError is null) _finalSnapshot = PrepareSnapshot(null);
            }
            catch (Exception error) { Fail("final snapshot boundary failed", error); }
            try { _writer.Dispose(); }
            catch (Exception error) { Fail("writer close failed", error); }
            finally
            {
                try { _stream.Dispose(); }
                catch (Exception error) { Fail("stream close failed", error); }
                LogWriterDiagnostic(maxWriteTicks, stalls);
                if (LostBytes > 0) AppLog.Error($"Audio capture lost samples: {Title}, lostMs={BytesToMilliseconds(LostBytes):0.###}.");
                _writerExited.TrySetResult();
            }
        }
    }

    private void LogWriterDiagnostic(long maxWriteTicks, long stalls)
    {
        long peak;
        lock (_queueGate) { peak = _peakQueuedBytes; _peakQueuedBytes = _queuedBytes; }
        AppLog.Debug($"Audio writer diag: {Title}, queuedMs={BytesToMilliseconds(QueuedBytes):0.###}, peakQueuedMs={BytesToMilliseconds(peak):0.###}, stalls={stalls}, maxWriterMs={maxWriteTicks * 1000d / Stopwatch.Frequency:0.###}, maxCallbackMs={Interlocked.Exchange(ref _maxCallbackTicks, 0) * 1000d / Stopwatch.Frequency:0.###}, lostMs={BytesToMilliseconds(LostBytes):0.###}.");
    }

    private Task<T> EnqueueControl<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueGate)
        {
            if (_closed) return Task.FromException<T>(new InvalidOperationException("Audio writer queue is closed."));
            _queue.Add(new Work(null, 0, default, null, () =>
            {
                try
                {
                    if (_writerError is not null) throw new IOException("Audio writer failed.", _writerError);
                    completion.TrySetResult(action());
                }
                catch (Exception error) { completion.TrySetException(error); }
            }));
        }
        return completion.Task;
    }

    public void Dispose()
    {
        lock (_queueGate)
        {
            if (_disposeStarted == 0)
            {
                _disposeStarted = 1;
                _stopTask = Task.Run(async () =>
                {
                    try { _capture.StopRecording(); }
                    catch (Exception error) { Fail("capture stop failed", error); }
                    // StopRecording may return before its final callback. Never
                    // unsubscribe or close the queue until that callback finishes.
                    await _captureStopped.Task.ConfigureAwait(false);
                    _capture.DataAvailable -= Capture_OnDataAvailable;
                    _capture.RecordingStopped -= Capture_OnRecordingStopped;
                    try { _capture.Dispose(); }
                    catch (Exception error) { Fail("capture dispose failed", error); }
                });
            }
        }
        try { Task.WhenAll(_stopTask!, _writerExited.Task).WaitAsync(_waitTimeout).GetAwaiter().GetResult(); }
        catch (TimeoutException error) { Fail("shutdown timeout; workers retain resources until completion", error); }
    }

    public bool SnapshotTo(string path, DateTime? earliestNeededUtc, out DateTime lastSampleUtc, AudioSnapshotPurpose purpose = AudioSnapshotPurpose.InteractiveReplay)
    {
        lastSampleUtc = MonotonicClock.UtcNow;
        try
        {
            Task<Snapshot> boundary;
            lock (_queueGate)
                boundary = _closed ? FinalSnapshotAsync() : EnqueueControl(() => PrepareSnapshot(earliestNeededUtc));
            var snapshot = boundary.WaitAsync(_waitTimeout).GetAwaiter().GetResult();
            lastSampleUtc = snapshot.LastSampleUtc;
            // Barrier fixes the byte range. Bulk disk copying runs outside the
            // writer so later packets continue to drain and append.
            using Stream source = snapshot.SourcePath is { } sourcePath
                ? new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : new MemoryStream(snapshot.Memory!, writable: false);
            source.Position = snapshot.Offset;
            WriteTailWav(path, source, snapshot.Count, purpose);
            return true;
        }
        catch (Exception error)
        {
            Fail("snapshot failed", error);
            return false;
        }
    }

    private async Task<Snapshot> FinalSnapshotAsync()
    {
        await _writerExited.Task.ConfigureAwait(false);
        return _finalSnapshot ?? throw new IOException("Audio writer could not finalize its snapshot.", _writerError);
    }

    private Snapshot PrepareSnapshot(DateTime? earliestNeededUtc)
    {
        try { _writer.Flush(); }
        catch (Exception error) { _writerError = error; Volatile.Write(ref _rejectPackets, 1); Fail("snapshot flush failed", error); throw; }
        var last = FirstSampleUtc is { } first
            ? first.AddSeconds(_bytesWritten / (double)Math.Max(1, AverageBytesPerSecond))
            : MonotonicClock.UtcNow;
        const double marginSeconds = 10;
        var keepBytes = long.MaxValue;
        if (earliestNeededUtc is { } earliest)
        {
            var seconds = (last - earliest).TotalSeconds + marginSeconds;
            keepBytes = seconds > 0 && seconds < long.MaxValue / Math.Max(1, AverageBytesPerSecond)
                ? (long)(seconds * AverageBytesPerSecond)
                : (long)(marginSeconds * AverageBytesPerSecond);
        }
        var skip = Math.Max(0, _bytesWritten - keepBytes);
        skip -= skip % Math.Max(1, _format.BlockAlign);
        var offset = _stream.Position - _bytesWritten + skip;
        if (_sourcePath is not null) return new Snapshot(_sourcePath, null, offset, _bytesWritten - skip, last);
        var memory = (MemoryStream)_stream;
        var copy = memory.GetBuffer().AsSpan(checked((int)offset), checked((int)(_bytesWritten - skip))).ToArray();
        return new Snapshot(null, copy, 0, copy.Length, last);
    }

    public void TrimTo(TimeSpan retention)
    {
        if (!IsMemoryBacked) return;
        try { EnqueueControl(() => { TrimCore(retention); return true; }).WaitAsync(_waitTimeout).GetAwaiter().GetResult(); }
        catch (Exception error) { Fail("memory trim failed", error); }
    }

    private void WriteTailWav(string path, Stream source, long dataBytes, AudioSnapshotPurpose purpose)
    {
        var background = UsesBackgroundIo(purpose) &&
            SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        try
        {
            using var destination = _snapshotOutput(path);
            using var writer = new WaveFileWriter(destination, _format);
            var buffer = new byte[256 * 1024];
            var remaining = dataBytes;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new EndOfStreamException("Audio snapshot source ended before its barrier boundary.");
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            if (background) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    // Compact on the writer only, with 15 seconds of slack to avoid copying
    // the retained memory buffer on every route timer tick.
    private const int CompactSlackSeconds = 15;

    private void TrimCore(TimeSpan retention)
    {
        if (_stream is not MemoryStream oldStream) return;

        var format = _format;
        var maxBytes = (long)(retention.TotalSeconds * format.AverageBytesPerSecond);
        maxBytes -= maxBytes % Math.Max(1, format.BlockAlign);
        if (_bytesWritten <= maxBytes) return;

        var trimBytes = _bytesWritten - maxBytes;
        trimBytes -= trimBytes % Math.Max(1, format.BlockAlign);
        if (trimBytes <= 0) return;

        var slackBytes = (long)format.AverageBytesPerSecond * CompactSlackSeconds;
        slackBytes -= slackBytes % Math.Max(1, format.BlockAlign);
        if (trimBytes < slackBytes) return;

        try
        {

            // WaveFileWriter writes a header (44 bytes for plain PCM,
            // larger for float/extensible formats) then the raw sample
            // data - rather than assume a fixed size, derive it from
            // what's actually there: whatever's left after subtracting
            // the PCM byte count this class already tracks separately
            // (_bytesWritten, updated on every real write and silence
            // backfill elsewhere in this class).
            var headerSize = (int)(oldStream.Length - _bytesWritten);
            var oldBuffer = oldStream.GetBuffer();
            var keepBytes = (int)(_bytesWritten - trimBytes);

            var newStream = new MemoryStream(keepBytes + headerSize + 4096);
            var newWriter = new WaveFileWriter(newStream, format);
            newWriter.Write(oldBuffer, headerSize + (int)trimBytes, keepBytes);

            _writer.Dispose();
            oldStream.Dispose();
            _stream = newStream;
            _writer = newWriter;
            _bytesWritten = keepBytes;

            if (FirstSampleUtc is { } first)
            {
                FirstSampleUtc = first.AddSeconds(trimBytes / (double)format.AverageBytesPerSecond);
            }
        }
        catch (Exception error)
        {
            Fail("audio RAM buffer trim failed", error);
            throw;
        }
    }

    private void WritePacket(WaveInEventArgs e, DateTime arrivalUtc)
    {
        // Timestamped path (ProcessLoopbackWaveIn, i.e. Game/Chat process
        // captures): every packet carries the exact wall-clock moment its
        // first frame was captured, so bytes are PLACED at their true
        // timeline offset - silence gaps land exactly where the source was
        // silent. This replaces the byte-count deficit heuristic, which
        // drifted hundreds of ms over long sessions (bursty/pre-rolled
        // delivery makes cumulative byte math lie in both directions) and
        // audibly desynced saved clips.
        if (e is TimestampedWaveInEventArgs timestamped)
        {
            if (!_firstSampleSeen)
            {
                _firstSampleSeen = true;
                FirstSampleUtc = timestamped.PacketStartUtc;
            }

            var format = _format;
            var expectedBytes = (long)((timestamped.PacketStartUtc - FirstSampleUtc!.Value).TotalSeconds * format.AverageBytesPerSecond);
            expectedBytes -= expectedBytes % Math.Max(1, format.BlockAlign);
            var aheadBytes = expectedBytes - _bytesWritten;
            // ~30ms tolerance: QPC timestamps are exact but packet sizes
            // quantize placement; below this it's jitter, not a gap.
            var toleranceBytes = format.AverageBytesPerSecond * 30 / 1000;
            if (aheadBytes > toleranceBytes)
            {
                WriteZeros(aheadBytes);
                if (aheadBytes > format.AverageBytesPerSecond / 4)
                {
                    AppLog.Info($"Audio capture gap placed from packet timestamps: {Title}, gap={aheadBytes * 1000L / Math.Max(1, format.AverageBytesPerSecond)}ms.");
                }
            }
            var bufferOffset = 0;
            var bytesToWrite = e.BytesRecorded;
            if (aheadBytes < 0)
            {
                // Skip timestamp-overlapping packet prefix. Writing it would
                // duplicate timeline time and delay all later mic audio.
                var overlapBytes = Math.Min(e.BytesRecorded, -aheadBytes);
                overlapBytes -= overlapBytes % Math.Max(1, format.BlockAlign);
                bufferOffset = (int)overlapBytes;
                bytesToWrite -= bufferOffset;
                if (aheadBytes < -toleranceBytes && !_loggedOverlap)
                {
                    _loggedOverlap = true;
                    AppLog.Info($"Audio capture packet overlap trimmed: {Title}, behindMs={-aheadBytes * 1000L / Math.Max(1, format.AverageBytesPerSecond)}.");
                }
            }

            if (bytesToWrite > 0)
            {
                _writer.Write(e.Buffer, bufferOffset, bytesToWrite);
                _bytesWritten += bytesToWrite;
            }
            AccumulatePeak(e.Buffer, e.BytesRecorded);
            LogPlacementDiagnostic(timestamped.PacketStartUtc);

            return;
        }

        var now = arrivalUtc;
        if (!_firstSampleSeen)
        {
            _firstSampleSeen = true;
            FirstSampleUtc = now;
        }

        _writer.Write(e.Buffer, 0, e.BytesRecorded);
        _bytesWritten += e.BytesRecorded;
        // Deficit check AFTER appending the current buffer, against plain
        // "now" - a pre-write check that subtracts the current buffer's
        // duration hides a steady shortage under the delivery buffer
        // size. Only used for NAudio endpoint/mic captures now; process
        // loopback uses the exact timestamped path above.
        WriteSilenceForDeliveryGap(now);
        AccumulatePeak(e.Buffer, e.BytesRecorded);
        LogPlacementDiagnostic(now);
    }

    private bool _loggedOverlap;
    private DateTime _nextPlacementDiagUtc = DateTime.MinValue;

    // Loudest absolute sample since the last placement diag - answers "is
    // this capture receiving actual signal or an active-but-silent stream?"
    // (a chat capture once delivered packets for a full hour whose content
    // was pure silence while voice audibly played; nothing in the logs could
    // distinguish that from a genuinely quiet call). 32-bit here is the
    // float mix format every one of these captures uses.
    private float _diagPeak;

    private void AccumulatePeak(byte[] buffer, int bytes)
    {
        if (_format.BitsPerSample != 32) return;
        for (var offset = 0; offset + 4 <= bytes; offset += 4)
        {
            var sample = Math.Abs(BitConverter.ToSingle(buffer, offset));
            if (sample > _diagPeak) _diagPeak = sample;
        }
    }

    // Once-a-minute per capture: how far the WAV's written length sits from
    // the wall-clock span it should cover. Near zero = saved clips will be in
    // sync; a growing value points straight at the capture kind responsible.
    private void LogPlacementDiagnostic(DateTime referenceUtc)
    {
        if (referenceUtc < _nextPlacementDiagUtc) return;
        _nextPlacementDiagUtc = referenceUtc + TimeSpan.FromSeconds(60);
        if (FirstSampleUtc is not { } first) return;
        var wallMs = (referenceUtc - first).TotalMilliseconds;
        var writtenMs = BytesToMilliseconds(_bytesWritten);
        var peakDb = _diagPeak > 0 ? 20 * Math.Log10(_diagPeak) : -120;
        _diagPeak = 0;
        AppLog.Debug($"Audio placement diag: {Title}, written={writtenMs / 1000:0.0}s, wall={wallMs / 1000:0.0}s, deficitMs={wallMs - writtenMs:0}, peakDb={peakDb:0}.");

        var clockOffset = MonotonicClock.SystemClockOffset;
        if (!_loggedClockStep && Math.Abs(clockOffset.TotalSeconds) > 2)
        {
            _loggedClockStep = true;
            AppLog.Info($"System clock step detected: wall clock is {clockOffset.TotalMilliseconds:0}ms away from the capture timeline. Capture alignment is unaffected (monotonic timebase).");
        }
    }

    private static bool _loggedClockStep;

    private void WriteZeros(long gapBytes)
    {
        // Never let a silence backfill push the WAV over the 4GiB RIFF cap -
        // WaveFileWriter throws "WAV file too large" there and the throw
        // kills the capture (or fails the snapshot pad, losing the whole
        // track from a save). A quiet process capture can accumulate a huge
        // pad-to-now gap; clamp to remaining capacity and let the pipeline's
        // rollover replace the capture.
        var capacityBytes = uint.MaxValue - 4096L - _bytesWritten;
        gapBytes = Math.Min(gapBytes, Math.Max(0, capacityBytes));
        gapBytes -= gapBytes % Math.Max(1, _format.BlockAlign);
        if (gapBytes <= 0) return;

        var zeros = new byte[Math.Min(gapBytes, 64 * 1024)];
        var remaining = gapBytes;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(zeros.Length, remaining);
            _writer.Write(zeros, 0, chunk);
            remaining -= chunk;
        }

        _bytesWritten += gapBytes;
    }

    private long _bytesWritten;

    private double BytesToMilliseconds(long bytes) =>
        bytes * 1000.0 / Math.Max(1, _format.AverageBytesPerSecond);

    // Endpoint (speaker) loopback and mic captures deliver a continuous
    // stream - silence included - so their WAV's timeline always matches
    // wall-clock. PROCESS loopback (the Chat tracks) only delivers buffers
    // while the target app is actually rendering audio: every stretch where
    // Discord etc. goes quiet is simply MISSING from the file, making the WAV
    // shorter than the wall-clock span it covers. Both the start- and
    // end-anchored WAV-position math in AudioCapturePipeline assume a
    // continuous timeline, so those holes shifted every chat sound after the
    // first gap to the wrong spot in saved clips (or into apparent silence).
    // Backfill each gap with actual zero samples as it's detected, keeping
    // WAV time == wall time for every capture kind.
    private void WriteSilenceForDeliveryGap(DateTime expectedDataStartUtc, double minGapMs = 300)
    {
        if (FirstSampleUtc is not { } firstSampleUtc) return;
        var expectedMs = (expectedDataStartUtc - firstSampleUtc).TotalMilliseconds;
        var writtenMs = BytesToMilliseconds(_bytesWritten);
        var gapMs = expectedMs - writtenMs;
        // Small jitter between deliveries is normal; only real gaps count.
        // (Snapshots pass a much tighter threshold - there the pad IS the
        // end-anchor, so any unfilled remainder becomes anchor error.)
        if (gapMs < minGapMs) return;

        var format = _format;
        var gapBytes = (long)(gapMs / 1000.0 * format.AverageBytesPerSecond);
        gapBytes -= gapBytes % Math.Max(1, format.BlockAlign);
        if (gapBytes <= 0) return;

        WriteZeros(gapBytes);
        if (gapMs >= 300) AppLog.Info($"Audio capture gap backfilled with silence: {Title}, gap={gapMs / 1000.0:0.0}s.");
    }
}
