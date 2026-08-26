using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace ClypDat.App.Services;

// Live microphone noise suppression, sitting between MicrophoneWaveIn and the
// AudioCaptureSession that writes the replay buffer. Wraps another IWaveIn and
// pipes its PCM through a long-lived ffmpeg running RNNoise (the `arnndn`
// filter) plus an optional noise gate, then re-raises the cleaned audio as if
// it had come straight off the device.
//
// Why a child process rather than libavfilter in-process: ClypDat bundles the
// STATIC ffmpeg.exe and only the avcodec/avformat/avutil/swresample/swscale
// shared DLLs for FFmpeg.AutoGen. There is no avfilter DLL, and adding one
// means shipping tens of megabytes for a single filter. The pipe costs one
// process and a few milliseconds while the mic is armed.
//
// The timeline is preserved exactly. arnndn is 1:1 in sample count (measured:
// 5s of 48kHz mono in, identical byte count out; same for stereo), so output
// time equals input time, and every output packet is stamped from the input
// packet timeline rather than from when the read happened to return. That
// matters because the whole point of MicrophoneWaveIn is that its packets
// carry WASAPI's own QPC capture instant - re-deriving timestamps from the
// pipe would throw that away and reintroduce the sync drift it exists to fix.
[SupportedOSPlatform("windows")]
internal sealed class DenoisingWaveIn : IWaveIn
{
    // RNNoise is a 48kHz model. Rather than refuse a device running at any
    // other rate, the graph resamples on the way in and this stage simply
    // reports 48kHz as its own format - the save pipeline resamples everything
    // to 48kHz anyway, so arriving there already at 48kHz costs nothing.
    private const int ModelSampleRate = 48000;

    // One failure disables wrapping for the rest of the session. The pipeline
    // reaps a dead capture and starts a fresh one for the same source, so
    // without this a machine where the pipe cannot run would rebuild it on a
    // loop instead of just recording a clean, unfiltered microphone.
    private static int _disabledForSession;

    public static bool IsDisabledForSession => Volatile.Read(ref _disabledForSession) != 0;

    private readonly IWaveIn _inner;
    private readonly string _filterChain;
    private readonly BlockingCollection<byte[]> _pending = new(new ConcurrentQueue<byte[]>(), 256);
    private readonly object _timelineLock = new();
    // (input seconds elapsed, wall clock of that instant) for each input
    // packet, used to stamp output packets. A cursor rather than a queue
    // because the anchor entry has to STAY readable after output has moved
    // past its start - it is the thing the offset is measured from.
    private readonly List<(double InputSeconds, DateTime Utc)> _timeline = new();
    private int _timelineCursor;
    private double _inputSeconds;
    private long _outputSamples;
    private Process? _process;
    private Thread? _writerThread;
    private Thread? _readerThread;
    private CancellationTokenSource? _cts;
    private bool _stopped;
    private int _droppedPackets;

    public DenoisingWaveIn(IWaveIn inner, double gateThresholdDb)
    {
        _inner = inner;
        _filterChain = BuildFilterChain(inner.WaveFormat.SampleRate, gateThresholdDb);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(ModelSampleRate, inner.WaveFormat.Channels);
        _inner.DataAvailable += Inner_OnDataAvailable;
        _inner.RecordingStopped += Inner_OnRecordingStopped;
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>
    /// Whether this device's format can be piped at all. Anything not 32-bit
    /// float would be reinterpreted as floats by the f32le input and come out
    /// as noise, so it is checked rather than assumed.
    ///
    /// Goes through AudioSampleFormat for the same reason the meter does: a
    /// driver reporting WAVE_FORMAT_EXTENSIBLE has an Encoding of Extensible,
    /// not IeeeFloat, and testing Encoding directly silently refused to filter
    /// an ordinary float microphone.
    /// </summary>
    public static bool CanWrap(WaveFormat format) =>
        AudioSampleFormat.IsFloat32(format)
        && format.Channels is 1 or 2
        && format.SampleRate > 0;

    private static string BuildFilterChain(int inputSampleRate, double gateThresholdDb)
    {
        var stages = new List<string>();
        if (inputSampleRate != ModelSampleRate)
        {
            stages.Add($"aresample={ModelSampleRate}");
        }

        stages.Add($"arnndn=m='{EscapeFilterPath(FfmpegPathResolver.RnnoiseModelPath)}'");

        // The gate runs AFTER the denoiser so its threshold is measured
        // against the cleaned signal - gating first would have it chasing the
        // noise floor RNNoise is about to remove. At the slider's floor the
        // gate would never close, so it is left out of the graph entirely
        // rather than added as a no-op stage.
        if (gateThresholdDb > MicrophoneNoiseSuppression.MinimumGateThresholdDb)
        {
            var linear = Math.Clamp(Math.Pow(10, gateThresholdDb / 20.0), 0, 1);
            stages.Add(string.Create(CultureInfo.InvariantCulture,
                $"agate=threshold={linear:0.########}:range=0.06:ratio=2:attack=20:release=250:detection=rms"));
        }

        return string.Join(",", stages);
    }

    // ffmpeg's filter argument parser treats \ : ' as syntax. Windows paths
    // carry the first two.
    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

    public void StartRecording()
    {
        _cts = new CancellationTokenSource();
        var startInfo = FfmpegPathResolver.Ffmpeg();
        foreach (var argument in BuildArguments()) startInfo.ArgumentList.Add(argument);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg did not start for microphone noise suppression.");
        try
        {
            // Above the capture threads, not below them: audio the user is
            // speaking into right now must not lose to a background encode.
            _process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
            // Priority is a nice-to-have.
        }

        var token = _cts.Token;
        _writerThread = new Thread(() => WriteLoop(token))
        {
            IsBackground = true,
            Name = "ClypDat mic denoise write"
        };
        _readerThread = new Thread(() => ReadLoop(token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ClypDat mic denoise read"
        };
        _writerThread.Start();
        _readerThread.Start();

        _inner.StartRecording();
        AppLog.Info($"Mic noise suppression active: filters='{_filterChain}', in={_inner.WaveFormat.SampleRate}Hz/{_inner.WaveFormat.Channels}ch.");
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "-hide_banner";
        // Without this ffmpeg treats the inherited console handle as an
        // interactive keyboard and can consume the parent's stdin.
        yield return "-nostdin";
        yield return "-v";
        yield return "error";
        yield return "-f";
        yield return "f32le";
        yield return "-ar";
        yield return _inner.WaveFormat.SampleRate.ToString(CultureInfo.InvariantCulture);
        yield return "-ac";
        yield return _inner.WaveFormat.Channels.ToString(CultureInfo.InvariantCulture);
        yield return "-i";
        yield return "pipe:0";
        yield return "-af";
        yield return _filterChain;
        yield return "-f";
        yield return "f32le";
        yield return "-ar";
        yield return ModelSampleRate.ToString(CultureInfo.InvariantCulture);
        yield return "-ac";
        yield return WaveFormat.Channels.ToString(CultureInfo.InvariantCulture);
        // Live stage: hand every packet over as soon as it exists instead of
        // letting the muxer accumulate. Buffered output would show up as the
        // microphone lagging the rest of the clip.
        yield return "-flush_packets";
        yield return "1";
        yield return "pipe:1";
    }

    private void Inner_OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var packet = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, packet, 0, e.BytesRecorded);

        var packetUtc = e is TimestampedWaveInEventArgs stamped ? stamped.PacketStartUtc : MonotonicClock.UtcNow;
        lock (_timelineLock)
        {
            _timeline.Add((_inputSeconds, packetUtc));
            _inputSeconds += e.BytesRecorded / (double)_inner.WaveFormat.AverageBytesPerSecond;
        }

        // Bounded and non-blocking: this runs on the WASAPI capture thread,
        // and blocking it because ffmpeg stalled would drop live microphone
        // audio at the device instead of just here, where it is visible.
        if (!_pending.TryAdd(packet) && Interlocked.Increment(ref _droppedPackets) % 50 == 1)
        {
            AppLog.Error($"Mic noise suppression backlog full; dropped {Volatile.Read(ref _droppedPackets)} packet(s).");
        }
    }

    private void Inner_OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // The device went away. Close the pipe so the reader drains whatever
        // is still inside ffmpeg, then report the stop upward.
        CompletePending();
        RecordingStopped?.Invoke(this, e);
    }

    private void WriteLoop(CancellationToken token)
    {
        var stdin = _process?.StandardInput.BaseStream;
        if (stdin is null) return;

        try
        {
            foreach (var packet in _pending.GetConsumingEnumerable(token))
            {
                stdin.Write(packet, 0, packet.Length);
                stdin.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception error)
        {
            if (!_stopped) Fail("write", error);
        }
        finally
        {
            // EOF is what makes ffmpeg flush its filter chain and exit, which
            // is what lets the reader below see the tail of the audio.
            try { stdin.Close(); } catch { /* already gone */ }
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        var stdout = _process?.StandardOutput.BaseStream;
        if (stdout is null) return;

        var blockAlign = WaveFormat.BlockAlign;
        var buffer = new byte[64 * 1024];
        // Output is handed on in whole frames only; a partial frame at a read
        // boundary is carried into the next read.
        var carry = new byte[blockAlign];
        var carryLength = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = stdout.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                var offset = 0;
                if (carryLength > 0)
                {
                    var need = Math.Min(blockAlign - carryLength, read);
                    Buffer.BlockCopy(buffer, 0, carry, carryLength, need);
                    carryLength += need;
                    offset = need;
                    if (carryLength == blockAlign)
                    {
                        carryLength = 0;
                        Emit(carry, blockAlign);
                    }
                }

                var usable = (read - offset) / blockAlign * blockAlign;
                if (usable > 0)
                {
                    var packet = new byte[usable];
                    Buffer.BlockCopy(buffer, offset, packet, 0, usable);
                    Emit(packet, usable);
                }

                var leftover = read - offset - usable;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buffer, offset + usable, carry, 0, leftover);
                    carryLength = leftover;
                }
            }
        }
        catch (Exception error)
        {
            if (!_stopped) Fail("read", error);
        }
    }

    private void Emit(byte[] packet, int bytes)
    {
        var samples = bytes / WaveFormat.BlockAlign;
        DateTime packetUtc;
        lock (_timelineLock)
        {
            var outputSeconds = _outputSamples / (double)ModelSampleRate;
            _outputSamples += samples;

            // Walk to the input packet this output actually came from, then
            // offset inside it. Anchoring to the device's own timestamps this
            // way keeps the microphone aligned even as the device clock drifts
            // from a nominal 48kHz over a long session.
            while (_timelineCursor + 1 < _timeline.Count
                   && _timeline[_timelineCursor + 1].InputSeconds <= outputSeconds)
            {
                _timelineCursor++;
            }

            if (_timelineCursor >= _timeline.Count)
            {
                // Output before any input was recorded, which only happens if
                // ffmpeg emits priming samples ahead of the first packet.
                packetUtc = MonotonicClock.UtcNow;
            }
            else
            {
                var anchor = _timeline[_timelineCursor];
                packetUtc = anchor.Utc + TimeSpan.FromSeconds(outputSeconds - anchor.InputSeconds);
            }

            // Consumed entries are dropped in batches rather than one at a
            // time: an hours-long session would otherwise grow this list by
            // one entry per 10ms packet and never give any of it back.
            if (_timelineCursor > 1024)
            {
                _timeline.RemoveRange(0, _timelineCursor);
                _timelineCursor = 0;
            }
        }

        DataAvailable?.Invoke(this, new TimestampedWaveInEventArgs(packet, bytes, packetUtc));
    }

    private void Fail(string stage, Exception error)
    {
        if (Interlocked.Exchange(ref _disabledForSession, 1) == 0)
        {
            AppLog.Error($"Mic noise suppression {stage} failed; recording microphones unfiltered for the rest of this session.", error);
        }

        // Reported as a capture death so AudioCapturePipeline's route timer
        // reaps this capture and starts a fresh one - which, with the flag
        // above set, comes back as a plain unfiltered microphone.
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    private void CompletePending()
    {
        try { _pending.CompleteAdding(); } catch { /* already completed */ }
    }

    public void StopRecording()
    {
        if (_stopped) return;
        _stopped = true;

        _inner.StopRecording();
        // Ordered: stop feeding, let the writer close stdin, let ffmpeg flush
        // its filter chain, let the reader drain it - THEN cancel. Cancelling
        // first would throw away the tail of the recording sitting inside the
        // filter graph.
        CompletePending();
        _writerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _cts?.Cancel();

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Teardown is best effort.
            }
        }
    }

    public void Dispose()
    {
        StopRecording();
        _inner.DataAvailable -= Inner_OnDataAvailable;
        _inner.RecordingStopped -= Inner_OnRecordingStopped;
        _inner.Dispose();
        try { _process?.Dispose(); } catch { /* already gone */ }
        _cts?.Dispose();
        _pending.Dispose();
    }
}
