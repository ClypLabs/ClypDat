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
    private readonly uint _processId;
    private readonly ProcessLoopbackCaptureMode _mode;
    private readonly IAudioClient _audioClient;
    private readonly IAudioCaptureClientNative _captureClient;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private bool _loggedFirstPacket;

    public ProcessLoopbackWaveIn(int processId, ProcessLoopbackCaptureMode mode)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _processId = (uint)processId;
        _mode = mode;
        _audioClient = ActivateAudioClient(_processId, _mode);
        WaveFormat = GetSharedRenderFormat();

        InitializeClient();

        object service;
        var captureClientGuid = AudioCaptureClientGuid;
        Marshal.ThrowExceptionForHR(_audioClient.GetService(captureClientGuid, out service));
        _captureClient = (IAudioCaptureClientNative)service;
        AppLog.Info($"Process loopback initialized: pid={_processId}, mode={_mode}, format={WaveFormat.SampleRate}Hz/{WaveFormat.Channels}ch/{WaveFormat.BitsPerSample}bit.");
    }

    // Ask WASAPI for a capture buffer big enough to survive a scheduling
    // stall instead of taking the engine default (one device period, ~10ms in
    // shared mode). With a 10ms buffer drained by a 10ms poll loop there was
    // no margin at all: any hiccup longer than a single period overran the
    // buffer and that audio was gone. Capture is written straight to a rolling
    // RAM buffer, so a deep capture buffer costs nothing that matters here -
    // there is no latency budget to protect.
    private const long BufferDuration100ns = 500 * 10_000L;

    private void InitializeClient()
    {
        var sessionGuid = Guid.Empty;
        Exception? lastError = null;
        // Loopback before None (process loopback is the whole point of this
        // class), deep buffer before the default within each - if a driver
        // rejects the requested size, a working capture at the default size
        // still beats no capture.
        foreach (var flags in new[] { AudioClientStreamFlags.Loopback, AudioClientStreamFlags.None })
        {
            foreach (var duration in new[] { BufferDuration100ns, 0L })
            {
                try
                {
                    Marshal.ThrowExceptionForHR(_audioClient.Initialize(
                        AudioClientShareMode.Shared,
                        flags,
                        duration,
                        0,
                        WaveFormat,
                        ref sessionGuid));
                    return;
                }
                catch (Exception error)
                {
                    lastError = error;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("Process loopback client could not be initialized.");
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        // A dedicated thread, not the thread pool. Draining the capture buffer
        // on time is the one thing this loop has to do, and a pool work item
        // queues behind whatever else the app is doing (encode, save, UI) on a
        // machine that is already out of CPU. See MmcssScope for the rest.
        _captureThread = new Thread(() => CaptureLoop(token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"ClypDat process loopback {_processId}"
        };
        _captureThread.Start();
    }

    public void StopRecording()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try
        {
            _captureThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Stop is best effort.
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _captureThread = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        if (_captureClient is not null) Marshal.ReleaseComObject(_captureClient);
        if (_audioClient is not null) Marshal.ReleaseComObject(_audioClient);
    }

    private void CaptureLoop(CancellationToken token)
    {
        Exception? stoppedError = null;
        var loggedDiscontinuity = false;
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
            Marshal.ThrowExceptionForHR(_audioClient.Start());
            while (!token.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0)
                {
                    Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(
                        out var data,
                        out var frames,
                        out var flags,
                        out _,
                        out var qpcPosition));

                    var bytes = frames * WaveFormat.BlockAlign;
                    var buffer = new byte[bytes];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent) && data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    _captureClient.ReleaseBuffer(frames);
                    if (bytes > 0)
                    {
                        if (!_loggedFirstPacket)
                        {
                            _loggedFirstPacket = true;
                            AppLog.Debug($"Process loopback first packet: pid={_processId}, mode={_mode}, frames={frames}, bytes={bytes}, silent={flags.HasFlag(AudioClientBufferFlags.Silent)}.");
                        }

                        if (!loggedDiscontinuity && flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
                        {
                            loggedDiscontinuity = true;
                            AppLog.Info($"Process loopback data discontinuity flagged: pid={_processId} - per-packet timestamps keep placement correct through it.");
                        }

                        // Exact capture moment of this packet's first frame -
                        // the ground truth AudioCaptureSession places bytes
                        // by, instead of guessing from byte counts and
                        // callback times (which drifted hundreds of ms over
                        // long sessions and desynced saved clips).
                        var packetStartUtc = utcBase + TimeSpan.FromTicks((long)(qpcPosition - qpcBase100ns));
                        QueueWithDeclick(buffer, bytes, packetStartUtc, flags.HasFlag(AudioClientBufferFlags.Silent) || data == IntPtr.Zero);
                    }
                    Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out packetFrames));
                }

                LogSilenceDiagnostic();
                Thread.Sleep(10);
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
            EmitPendingPacket(nextSilent: null);
            try
            {
                _audioClient.Stop();
            }
            catch
            {
                // Stop is best effort.
            }

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
    private int _silentRuns;
    private long _silentFrames;
    private DateTime _nextSilenceLogUtc = DateTime.MinValue;

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

        if (_pendingSilent)
        {
            if (!_previousSilent) _silentRuns++;
            _silentFrames += _pendingBytes / Math.Max(1, WaveFormat.BlockAlign);
        }
        else
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

    // How much of a capture is actually arriving as SILENT-flagged holes -
    // the thing the declick above is smoothing over. Logged only when there
    // are any, once a minute, so a clip that still sounds wrong can be checked
    // against how much the source stream was actually dropping.
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
        if (_silentRuns == 0) return;
        var silentMs = _silentFrames * 1000.0 / Math.Max(1, WaveFormat.SampleRate);
        AppLog.Debug($"Process loopback silent-packet holes: pid={_processId}, runs={_silentRuns}, silentMs={silentMs:0} (declicked).");
        _silentRuns = 0;
        _silentFrames = 0;
    }

    private static IAudioClient ActivateAudioClient(uint processId, ProcessLoopbackCaptureMode mode)
    {
        var activation = new AudioClientActivationParamsNative
        {
            ActivationType = 1,
            TargetProcessId = processId,
            ProcessLoopbackMode = mode == ProcessLoopbackCaptureMode.IncludeTargetProcessTree ? 0 : 1
        };
        var activationPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParamsNative>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobNative>());
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
            var handler = new ActivateAudioInterfaceCompletionHandler();
            var audioClientGuid = AudioClientGuid;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientGuid,
                propVariantPtr,
                handler,
                out _));
            return (IAudioClient)handler.WaitForResult();
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(activationPtr);
        }
    }

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

    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        }

        public object WaitForResult()
        {
            return _completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
}

internal enum ProcessLoopbackCaptureMode
{
    IncludeTargetProcessTree,
    ExcludeTargetProcessTree
}
