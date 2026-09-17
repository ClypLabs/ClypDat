using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ClypDat.App.Services;

// WaveInEventArgs plus the exact wall-clock capture moment of the packet's
// first frame (from WASAPI's per-packet QPC timestamp). AudioCaptureSession
// type-checks for this to place bytes at their true timeline offset instead
// of relying on byte-count accounting.
internal sealed class TimestampedWaveInEventArgs : WaveInEventArgs
{
    public TimestampedWaveInEventArgs(byte[] buffer, int bytes, DateTime packetStartUtc)
        : base(buffer, bytes)
    {
        PacketStartUtc = packetStartUtc;
    }

    public DateTime PacketStartUtc { get; }
}

[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackWaveIn : IWaveIn
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientGuid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientGuid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    // Only reached when a driver refused event-driven capture. Sleep granularity
    // is whatever the system timer resolution happens to be, so this is a
    // best-effort improvement on the 10ms that was overrunning a one-period
    // buffer, not a guarantee.
    private const int PollIntervalMs = 2;
    // How long a wake-up wait blocks before the loop runs its periodic work
    // anyway. A stream with nothing playing signals rarely or not at all, and
    // cancellation, the silence diagnostic and the drain counter all still
    // need to run.
    private const int PacketWaitTimeoutMs = 200;
    private const uint WaitObject0 = 0;
    private readonly uint _processId;
    private readonly ProcessLoopbackCaptureMode _mode;
    private readonly IProcessLoopbackClient _client;
    private readonly object _lifetime = new();
    private readonly TimeSpan _stopTimeout;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private bool _disposed;
    private bool _released;
    private bool _loggedFirstPacket;

    public ProcessLoopbackWaveIn(int processId, ProcessLoopbackCaptureMode mode)
        : this(new NativeClient(processId, mode), TimeSpan.FromSeconds(2))
    {
        _processId = (uint)processId;
        _mode = mode;
        AppLog.Info($"Process loopback initialized: pid={_processId}, mode={_mode}, format={WaveFormat}.");
    }

    internal ProcessLoopbackWaveIn(IProcessLoopbackClient client, TimeSpan stopTimeout)
    {
        _client = client;
        _stopTimeout = stopTimeout;
        WaveFormat = client.WaveFormat;
    }

    private sealed class NativeClient : IProcessLoopbackClient
    {
        private readonly IAudioClient _audioClient;
        private readonly IAudioCaptureClientNative _captureClient;
        private readonly int _processId;
        private IntPtr _captureEvent;
        public WaveFormat WaveFormat { get; }

        public NativeClient(int processId, ProcessLoopbackCaptureMode mode)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            _processId = processId;
            WaveFormat = GetSharedRenderFormat();
            // Event-driven first, polling second. A client that accepted
            // EVENTCALLBACK but then refused SetEventHandle is stuck - an
            // initialized IAudioClient cannot be initialized again - so the
            // fallback gets its own activation instead of inheriting a client
            // that can never be started.
            foreach (var eventDriven in new[] { true, false })
            {
                var client = ActivateAudioClient((uint)processId, mode);
                try
                {
                    if (eventDriven) _captureEvent = CreateCaptureEvent();
                    InitializeClient(client, eventDriven);
                    Marshal.ThrowExceptionForHR(client.GetService(AudioCaptureClientGuid, out var service));
                    _captureClient = (IAudioCaptureClientNative)service;
                    _audioClient = client;
                    return;
                }
                catch (Exception error)
                {
                    Marshal.ReleaseComObject(client);
                    CloseCaptureEvent();
                    if (!eventDriven) throw;
                    AppLog.Info($"Process loopback event-driven capture unavailable: pid={processId}, error={error.Message}; falling back to the poll loop.");
                }
            }

            // Unreachable: the last attempt rethrows rather than falling out.
            throw new InvalidOperationException("Process loopback client could not be initialized.");
        }

        public int Start() => _audioClient.Start();
        public int Stop() => _audioClient.Stop();
        public int GetNextPacketSize(out int frames) => _captureClient.GetNextPacketSize(out frames);
        public int GetBuffer(out IntPtr data, out int frames, out AudioClientBufferFlags flags, out long position, out long qpc)
            => _captureClient.GetBuffer(out data, out frames, out flags, out position, out qpc);
        public int ReleaseBuffer(int frames) => _captureClient.ReleaseBuffer(frames);

        // Event mode: WASAPI signals once per device period, so the loop wakes
        // exactly when there is something to drain and never sleeps through a
        // period. Polling fallback: the old fixed sleep.
        public bool WaitForPacket(int timeoutMs)
        {
            if (_captureEvent == IntPtr.Zero)
            {
                Thread.Sleep(Math.Min(timeoutMs, PollIntervalMs));
                return true;
            }

            return WaitForSingleObject(_captureEvent, (uint)timeoutMs) == WaitObject0;
        }

        public void Dispose()
        {
            try { Marshal.ReleaseComObject(_captureClient); }
            finally
            {
                try { Marshal.ReleaseComObject(_audioClient); }
                finally { CloseCaptureEvent(); }
            }
        }

        private static IntPtr CreateCaptureEvent()
        {
            // Auto-reset, initially unsignalled - what WASAPI expects from an
            // event-driven client.
            var handle = CreateEventW(IntPtr.Zero, false, false, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }

        private void CloseCaptureEvent()
        {
            var handle = Interlocked.Exchange(ref _captureEvent, IntPtr.Zero);
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }

        // hnsBufferDuration is advisory here, not a guarantee: process loopback
        // accepts the request and hands back one device period anyway (measured
        // on this machine as requestedMs=500 -> actualFrames=480, actualMs=10,
        // while an ordinary mic capture asking for the same 500ms got 24000
        // frames). A capture that only holds one period cannot be polled
        // safely - the poll loop's own wake-up jitter was measured at 12.5-14.5ms
        // against that 10ms buffer, so whole periods were being overrun and lost
        // as silent holes in the game track. Event-driven capture is the fix;
        // the request stays because a driver that does honour it costs nothing.
        private const long BufferDuration100ns = 500 * 10_000L;

        private void InitializeClient(IAudioClient client, bool eventDriven)
        {
            var sessionGuid = Guid.Empty;
            Exception? lastError = null;
            // Loopback before None (process loopback is the whole point of this
            // class), deep buffer before the default within each - if a driver
            // rejects the requested size, a working capture at the default size
            // still beats no capture.
            var baseFlags = eventDriven
                ? AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                : AudioClientStreamFlags.Loopback;
            var fallbackFlags = eventDriven
                ? AudioClientStreamFlags.EventCallback
                : AudioClientStreamFlags.None;
            foreach (var flags in new[] { baseFlags, fallbackFlags })
            {
                foreach (var duration in new[] { BufferDuration100ns, 0L })
                {
                    try
                    {
                        Marshal.ThrowExceptionForHR(client.Initialize(
                            AudioClientShareMode.Shared,
                            flags,
                            duration,
                            0,
                            WaveFormat,
                            ref sessionGuid));
                    }
                    catch (Exception error)
                    {
                        lastError = error;
                        AppLog.Info($"Process loopback buffer initialization failed: pid={_processId}, flags={flags}, requestedMs={duration / 10_000d:0.###}, error={error.Message}; trying fallback.");
                        continue;
                    }

                    // Past here the client is initialized, and an initialized
                    // IAudioClient cannot be initialized again - so a failure
                    // now ends this attempt for good and the constructor
                    // re-activates for the polling fallback. SetEventHandle
                    // must follow Initialize and precede Start, or Start fails
                    // with AUDCLNT_E_EVENTHANDLE_NOT_SET.
                    if (eventDriven) Marshal.ThrowExceptionForHR(client.SetEventHandle(_captureEvent));
                    var bufferResult = client.GetBufferSize(out var bufferFrames);
                    AppLog.Info($"Process loopback buffer initialized: pid={_processId}, flags={flags}, requestedMs={duration / 10_000d:0.###}, actualFrames={(bufferResult >= 0 ? bufferFrames.ToString() : "unknown")}, actualMs={(bufferResult >= 0 ? (bufferFrames * 1000d / WaveFormat.SampleRate).ToString("0.###") : "unknown")}.");
                    return;
                }
            }

            throw lastError ?? new InvalidOperationException("Process loopback client could not be initialized.");
        }

    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A timed-out stop still owns the client. Never overlap capture loops.
            if (_captureThread is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _captureThread = new Thread(() => RunCapture(token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"ClypDat process loopback {_processId}"
            };
            try { _captureThread.Start(); }
            catch
            {
                _captureThread = null;
                _cts.Dispose();
                _cts = null;
                throw;
            }
        }
    }

    public void StopRecording()
    {
        Thread? thread;
        lock (_lifetime)
        {
            _cts?.Cancel();
            thread = _captureThread;
        }
        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(_stopTimeout))
            AppLog.Info($"Process loopback audio stop timeout: pid={_processId}; capture thread retains resources until completion.");
    }

    public void Dispose()
    {
        bool release;
        lock (_lifetime)
        {
            _disposed = true;
            release = _captureThread is null && !_released;
            if (release) _released = true;
        }
        if (release) _client.Dispose();
        StopRecording();
    }

    private void RunCapture(CancellationToken token)
    {
        try { CaptureLoop(token); }
        catch (Exception error) { AppLog.Error("Process loopback callback failed", error); }
        finally
        {
            bool release;
            lock (_lifetime)
            {
                _cts?.Dispose();
                _cts = null;
                release = _disposed && !_released;
                if (release) _released = true;
                else _captureThread = null;
            }
            // Native release can also stall. Never hold the lifetime lock across
            // it; concurrent StopRecording must still reach its bounded Join.
            if (release)
            {
                try { _client.Dispose(); }
                catch (Exception error) { AppLog.Error("Process loopback release failed", error); }
                finally { lock (_lifetime) _captureThread = null; }
            }
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        Exception? stoppedError = null;
        var diagnostics = new AudioCaptureDiagnostics($"process loopback pid={_processId}");
        // Base pair for converting each packet's QPC capture timestamp (100ns
        // units of the performance counter) into UTC on the MonotonicClock
        // timeline. Stopwatch.GetTimestamp reads the same QPC, so the offset
        // between the two clocks is fixed for the process lifetime - and
        // MonotonicClock keeps every capture on one shared timeline that a
        // system clock step (NTP correction) can't shift mid-session.
        var utcBase = MonotonicClock.UtcNow;
        var qpcBase100ns = Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency);
        using var mmcss = MmcssScope.ProAudio($"process loopback pid={_processId}");
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(_client.Start());
            var signalled = true;
            while (!token.IsCancellationRequested)
            {
                if (signalled) diagnostics.Drain();
                else diagnostics.Idle();
                Marshal.ThrowExceptionForHR(_client.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0 && !token.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(_client.GetBuffer(
                        out var data,
                        out var frames,
                        out var flags,
                        out _,
                        out var qpcPosition));
                    diagnostics.Packet(flags);

                    var bytes = frames * WaveFormat.BlockAlign;
                    var buffer = new byte[bytes];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent) && data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    _client.ReleaseBuffer(frames);
                    if (bytes > 0)
                    {
                        if (!_loggedFirstPacket)
                        {
                            _loggedFirstPacket = true;
                            AppLog.Debug($"Process loopback first packet: pid={_processId}, mode={_mode}, frames={frames}, bytes={bytes}, silent={flags.HasFlag(AudioClientBufferFlags.Silent)}.");
                        }

                        // Exact capture moment of this packet's first frame -
                        // the ground truth AudioCaptureSession places bytes
                        // by, instead of guessing from byte counts and
                        // callback times (which drifted hundreds of ms over
                        // long sessions and desynced saved clips).
                        var packetStartUtc = utcBase + TimeSpan.FromTicks((long)(qpcPosition - qpcBase100ns));
                        var callbackStart = Stopwatch.GetTimestamp();
                        QueueWithDeclick(buffer, bytes, packetStartUtc, IsHole(buffer, bytes, flags, data));
                        diagnostics.Callback(callbackStart);
                    }
                    if (token.IsCancellationRequested) break;
                    Marshal.ThrowExceptionForHR(_client.GetNextPacketSize(out packetFrames));
                }

                LogSilenceDiagnostic();
                if (token.IsCancellationRequested) break;
                signalled = _client.WaitForPacket(PacketWaitTimeoutMs);
            }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) stoppedError = error;
        }
        finally
        {
            // The held-back packet still belongs in the capture - without this
            // every stop (route change, roll, session end) would silently drop
            // its last packet.
            try { EmitPendingPacket(nextSilent: null); }
            catch (Exception error) { stoppedError ??= error; }
            try
            {
                _client.Stop();
            }
            catch
            {
                // Stop is best effort.
            }

            diagnostics.Log();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(stoppedError));
        }
    }

    // WASAPI process loopback does not deliver a continuous stream: whenever
    // the captured tree has nothing queued for a device period it hands back a
    // packet flagged SILENT, whose buffer is left as digital zeros. On a 96kHz
    // endpoint that period is 5ms, and a measured clip had ~6 of these per
    // second scattered through audio that was playing continuously the whole
    // time - the zero-run lengths in the saved track came out as exact
    // multiples of 5ms. Each hole is a step discontinuity at both of its
    // edges, and a step is a click: that is the crackle a saved clip picks up
    // while the game itself sounds fine live.
    //
    // Ramping the audio into a hole and back out of it turns each one into a
    // dip nobody can hear. Doing that needs the tail of the OUTGOING packet
    // faded once the next packet reveals a hole is starting, so one packet is
    // always held back - 5ms of extra latency, which is nothing for a buffer
    // that is only read seconds later.
    private const double DeclickFadeMs = 1.5;

    private byte[]? _pendingBuffer;
    private int _pendingBytes;
    private DateTime _pendingStartUtc;
    private bool _pendingSilent;
    // Stream starts "in silence" so the very first packet fades in rather than
    // opening on a step from nothing.
    private bool _previousSilent = true;
    private readonly SilentHoleCounter _holes = new();
    private DateTime _nextSilenceLogUtc = DateTime.MinValue;

    // WASAPI process loopback flags only some of its holes. A measured Minecraft
    // capture had 10-20% of the stream arriving as whole zero-filled device
    // periods with clean flags - discontinuities=0, timestampErrors=0, and the
    // SILENT counter below had never fired once across a week of logs - while
    // those same zero runs sat in the WAV. Unflagged, they skipped the declick
    // and reached the file as a step at each edge, which is what the crackle
    // was. The payload is the only dependable signal, so test that too.
    private static bool IsHole(byte[] buffer, int bytes, AudioClientBufferFlags flags, IntPtr data)
        => flags.HasFlag(AudioClientBufferFlags.Silent) || data == IntPtr.Zero || IsSilentPayload(buffer, bytes);

    // Stops at the first non-zero byte, so audible audio costs a couple of
    // comparisons per packet.
    private static bool IsSilentPayload(byte[] buffer, int bytes)
        => buffer.AsSpan(0, Math.Clamp(bytes, 0, buffer.Length)).IndexOfAnyExcept((byte)0) < 0;

    private void QueueWithDeclick(byte[] buffer, int bytes, DateTime startUtc, bool silent)
    {
        EmitPendingPacket(silent);
        _pendingBuffer = buffer;
        _pendingBytes = bytes;
        _pendingStartUtc = startUtc;
        _pendingSilent = silent;
    }

    // nextSilent is null when nothing follows (the capture is stopping), which
    // is a hole of unbounded length - fade out for the same reason.
    private void EmitPendingPacket(bool? nextSilent)
    {
        var buffer = _pendingBuffer;
        if (buffer is null) return;
        _pendingBuffer = null;

        _holes.Packet(_pendingSilent, _previousSilent, _pendingBytes / Math.Max(1, WaveFormat.BlockAlign));
        if (!_pendingSilent)
        {
            if (_previousSilent) ApplyFade(buffer, _pendingBytes, fadeIn: true);
            if (nextSilent != false) ApplyFade(buffer, _pendingBytes, fadeIn: false);
        }

        _previousSilent = _pendingSilent;
        DataAvailable?.Invoke(this, new TimestampedWaveInEventArgs(buffer, _pendingBytes, _pendingStartUtc));
    }

    private void ApplyFade(byte[] buffer, int bytes, bool fadeIn)
    {
        var format = WaveFormat;
        // These captures are always the endpoint's 32-bit float mix format;
        // anything else is left alone rather than reinterpreted wrongly.
        if (format.BitsPerSample != 32) return;
        var blockAlign = Math.Max(1, format.BlockAlign);
        var frames = bytes / blockAlign;
        if (frames <= 0) return;
        var fadeFrames = Math.Min(frames, (int)(format.SampleRate * DeclickFadeMs / 1000.0));
        if (fadeFrames <= 1) return;

        for (var index = 0; index < fadeFrames; index++)
        {
            // Raised cosine, so neither end of the ramp is itself a corner -
            // a linear ramp only trades one discontinuity for two smaller ones
            // in the first derivative, which still ticks.
            var position = (index + 1.0) / (fadeFrames + 1.0);
            var gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (fadeIn ? position : 1.0 - position)));
            var frameOffset = (fadeIn ? index : frames - fadeFrames + index) * blockAlign;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = frameOffset + channel * 4;
                if (sampleOffset + 4 > bytes) break;
                var faded = BitConverter.ToSingle(buffer, sampleOffset) * gain;
                BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), faded);
            }
        }
    }

    // How much of a capture is actually arriving as holes - the thing the
    // declick above is smoothing over. Logged only when there are any, once a
    // minute, so a clip that still sounds wrong can be checked against how much
    // the source stream was actually dropping. silentMs/runs is the number that
    // separates the two causes: a run per device period (~10ms each, hundreds a
    // minute) is capture loss, while hundreds of ms per run is the source
    // genuinely playing nothing.
    private void LogSilenceDiagnostic()
    {
        var now = MonotonicClock.UtcNow;
        if (_nextSilenceLogUtc == DateTime.MinValue)
        {
            _nextSilenceLogUtc = now + TimeSpan.FromSeconds(60);
            return;
        }

        if (now < _nextSilenceLogUtc) return;
        _nextSilenceLogUtc = now + TimeSpan.FromSeconds(60);
        if (!_holes.TryTakeInterval(out var runs, out var silentFrames)) return;
        var silentMs = silentFrames * 1000.0 / Math.Max(1, WaveFormat.SampleRate);
        AppLog.Debug($"Process loopback silent-packet holes: pid={_processId}, runs={runs}, silentMs={silentMs:0} (declicked).");
    }

    private static IAudioClient ActivateAudioClient(uint processId, ProcessLoopbackCaptureMode mode)
    {
        var activation = new AudioClientActivationParamsNative
        {
            ActivationType = 1,
            TargetProcessId = processId,
            ProcessLoopbackMode = mode == ProcessLoopbackCaptureMode.IncludeTargetProcessTree ? 0 : 1
        };
        // ActivateAudioInterfaceAsync is ASYNCHRONOUS: Core Audio keeps reading
        // these buffers, and keeps its COM pointer to the handler, until it calls
        // back - which can be long after we have stopped waiting. Freeing them in
        // a finally around the wait handed Core Audio memory that had already
        // been returned to the heap, and a callback object nothing referenced any
        // more. It crashed the recorder well away from here, as a DEP execute
        // fault at an address in no module, or later as heap corruption. So the
        // handler owns all of it and releases it when the callback actually
        // arrives, timeout or not.
        var activationPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParamsNative>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobNative>());
        var handler = new ActivateAudioInterfaceCompletionHandler(activationPtr, propVariantPtr);
        try
        {
            Marshal.StructureToPtr(activation, activationPtr, false);
            var propVariant = new PropVariantBlobNative
            {
                VariantType = 65,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParamsNative>(),
                BlobData = activationPtr
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);
            var audioClientGuid = AudioClientGuid;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientGuid,
                propVariantPtr,
                handler,
                out _));
        }
        catch
        {
            // The activation never reached Core Audio, so nothing else can be
            // holding the buffers and reclaiming them here is safe.
            handler.AbandonBeforeActivation();
            throw;
        }

        try
        {
            return (IAudioClient)handler.WaitForResult();
        }
        catch (TimeoutException)
        {
            // Deliberately no free: the activation is still in flight. The handler
            // reclaims the buffers if the callback ever arrives, and leaking a few
            // dozen bytes beats handing Core Audio freed memory.
            AppLog.Info($"Process loopback activation timed out after {ActivationTimeout.TotalSeconds:0}s: pid={processId}, mode={mode}.");
            throw;
        }
    }

    // How long a caller waits for Core Audio to finish activating. Reaching it no
    // longer risks anything beyond the capture failing to start.
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private static WaveFormat GetSharedRenderFormat()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioClient.MixFormat;
        }
        catch
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParamsNative
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlobNative
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientNative
    {
        // [PreserveSig] on every member: without it the CLR treats the int return as an
        // HRESULT it should check and throw on, and marshals the declared return value
        // as an extra trailing out-parameter - so native GetBuffer was being called with
        // six arguments instead of five. On this x64-only build that happens to be
        // harmless (the caller cleans the stack, and the zero-initialised retval slot
        // makes the implicit ThrowExceptionForHR a no-op), but it is an ABI mismatch
        // that breaks on x86 and it contradicts the [PreserveSig] used on the sibling
        // interface a few lines up.
        [PreserveSig]
        int GetBuffer(
            out IntPtr data,
            out int numFramesToRead,
            out AudioClientBufferFlags bufferFlags,
            out long devicePosition,
            out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>
    /// Owns everything Core Audio borrows for the length of an activation: the two
    /// unmanaged buffers describing what to activate, and a root on itself, since
    /// Core Audio holds a COM pointer here until it calls back. All of it is
    /// released when the callback arrives, which may be after the caller has
    /// already given up and thrown.
    /// </summary>
    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GCHandle _self;
        private IntPtr _activationParams;
        private IntPtr _propVariant;

        public ActivateAudioInterfaceCompletionHandler(IntPtr activationParams, IntPtr propVariant)
        {
            _activationParams = activationParams;
            _propVariant = propVariant;
            _self = GCHandle.Alloc(this);
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var activateResult, out var activatedInterface);
                Marshal.ThrowExceptionForHR(activateResult);
                _completion.TrySetResult(activatedInterface);
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                // Core Audio is done reading the buffers by the time it reports a
                // result, and will not call back again.
                Release();
            }
        }

        public object WaitForResult()
        {
            return _completion.Task.WaitAsync(ActivationTimeout).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reclaim without waiting for a callback. Only correct when the
        /// activation call itself failed, so Core Audio never took ownership.
        /// </summary>
        public void AbandonBeforeActivation() => Release();

        private void Release()
        {
            var propVariant = Interlocked.Exchange(ref _propVariant, IntPtr.Zero);
            if (propVariant != IntPtr.Zero) Marshal.FreeHGlobal(propVariant);
            // Freed second: the PROPVARIANT points at it.
            var activationParams = Interlocked.Exchange(ref _activationParams, IntPtr.Zero);
            if (activationParams != IntPtr.Zero) Marshal.FreeHGlobal(activationParams);
            // Safe from inside the callback: `this` is live on the stack.
            if (_self.IsAllocated) _self.Free();
        }
    }
}

internal enum ProcessLoopbackCaptureMode
{
    IncludeTargetProcessTree,
    ExcludeTargetProcessTree
}

// Run and frame accounting for the silence diagnostic, split out so the
// once-an-interval reset can be tested without a capture thread.
internal sealed class SilentHoleCounter
{
    private int _runs;
    private long _frames;

    public void Packet(bool silent, bool previousSilent, int frames)
    {
        if (!silent) return;
        if (!previousSilent) _runs++;
        _frames += frames;
    }

    // Always clears, whether or not it reports. A stream that stays silent for
    // a whole interval records no run STARTS, so returning early on runs==0
    // without clearing carried its frames forward and the next interval that
    // did report counted them as its own - a paused Spotify reported
    // silentMs=327550 inside a 60s window.
    public bool TryTakeInterval(out int runs, out long frames)
    {
        runs = _runs;
        frames = _frames;
        _runs = 0;
        _frames = 0;
        return runs > 0;
    }
}

internal interface IProcessLoopbackClient : IDisposable
{
    WaveFormat WaveFormat { get; }
    int Start();
    int Stop();
    int GetNextPacketSize(out int frames);
    int GetBuffer(out IntPtr data, out int frames, out AudioClientBufferFlags flags, out long position, out long qpc);
    int ReleaseBuffer(int frames);
    // Blocks until the client has something to drain, or the timeout expires.
    // Returns whether a packet was actually signalled.
    bool WaitForPacket(int timeoutMs);
}
