using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClypDat.App.Services;

// Drives the Mic Test meter in Settings > Audio. Opens the selected capture
// device, runs it through exactly the same filter stage the replay buffer
// would (MicrophoneNoiseSuppression.Wrap), and reports a level in dBFS so the
// meter shows what would actually be recorded - including the gate opening and
// closing against the threshold the slider is set to.
//
// Deliberately a separate, short-lived capture rather than a tap on the replay
// buffer's own: the test has to work before the buffer is armed, on a device
// that is not the one currently being recorded, and with settings the user has
// not saved yet.
[SupportedOSPlatform("windows")]
internal sealed class MicrophoneLevelMonitor : IDisposable
{
    // Anything below this reads as silence on the meter. Matches the noise
    // gate slider's own floor so the two scales line up on screen.
    public const double FloorDb = MicrophoneNoiseSuppression.MinimumGateThresholdDb;

    private readonly object _lock = new();
    private IWaveIn? _capture;
    private MMDevice? _device;
    private double _smoothedDb = FloorDb;
    private long _packetsSeen;
    private float _peakSinceLastLog;

    /// <summary>Latest level in dBFS, already smoothed. Raised off the capture thread.</summary>
    public event EventHandler<double>? LevelChanged;

    public bool IsRunning { get { lock (_lock) return _capture is not null; } }

    public void Start(string deviceId, bool noiseSuppression, double gateThresholdDb)
    {
        Stop();

        lock (_lock)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDeviceOption.DefaultDeviceId
                    ? DefaultMicrophone.Get(enumerator)
                    : enumerator.GetDevice(deviceId);

                _smoothedDb = FloorDb;
                _packetsSeen = 0;
                _peakSinceLastLog = 0;
                var capture = MicrophoneNoiseSuppression.Wrap(
                    new MicrophoneWaveIn(_device),
                    noiseSuppression,
                    gateThresholdDb,
                    _device.FriendlyName);
                capture.DataAvailable += Capture_OnDataAvailable;
                capture.StartRecording();
                _capture = capture;
                AppLog.Info($"Mic test started: device={_device.FriendlyName}, denoise={noiseSuppression}, gate={gateThresholdDb:0.#}dB.");
            }
            catch (Exception error)
            {
                AppLog.Error("Mic test could not start.", error);
                StopLocked();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= Capture_OnDataAvailable;
            try { capture.StopRecording(); } catch { /* teardown is best effort */ }
            try { capture.Dispose(); } catch { /* teardown is best effort */ }
        }

        // MicrophoneWaveIn deliberately does not dispose the MMDevice's cached
        // AudioClient (see its Dispose), so the device object is this class's
        // to release.
        try { _device?.Dispose(); } catch { /* teardown is best effort */ }
        _device = null;

        if (capture is not null)
        {
            _smoothedDb = FloorDb;
            LevelChanged?.Invoke(this, FloorDb);
        }
    }

    private void Capture_OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = (sender as IWaveIn)?.WaveFormat;
        if (format is null || e.BytesRecorded <= 0) return;

        if (!AudioSampleFormat.TryGetPeak(format, e.Buffer, e.BytesRecorded, out var peak))
        {
            // Once per start, not per packet: an unreadable format does not
            // fix itself, and this runs ~100 times a second.
            if (Interlocked.Increment(ref _packetsSeen) == 1)
            {
                AppLog.Info(
                    $"Mic test cannot read this capture format ({AudioSampleFormat.ResolveEncoding(format)}, " +
                    $"{format.BitsPerSample}-bit); the meter will stay at its floor.");
            }

            return;
        }

        var db = peak <= 0 ? FloorDb : Math.Clamp(20 * Math.Log10(peak), FloorDb, 0);

        // Fast attack, slow release - the standard meter ballistics. A raw
        // per-packet peak at 10ms granularity flickers too hard to read, and
        // smoothing the attack as well would hide exactly the transients the
        // user is checking the gate against.
        _smoothedDb = db > _smoothedDb ? db : _smoothedDb + (db - _smoothedDb) * 0.25;
        LevelChanged?.Invoke(this, _smoothedDb);

        // Roughly once a second while the test runs. This is what makes "is
        // the meter actually reading my microphone?" answerable from the log
        // instead of by poking at private methods from outside the process.
        if (peak > _peakSinceLastLog) _peakSinceLastLog = peak;
        if (Interlocked.Increment(ref _packetsSeen) % 100 == 0)
        {
            AppLog.Debug($"Mic test level: peak={_peakSinceLastLog:0.####}, smoothed={_smoothedDb:0.#}dB.");
            _peakSinceLastLog = 0;
        }
    }

    public void Dispose() => Stop();
}
