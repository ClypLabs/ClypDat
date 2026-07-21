using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Eve.App.Services;

// Mic capture via NAudio's AudioClient/AudioCaptureClient directly (not
// NAudio's higher-level WasapiCapture) so every packet carries WASAPI's own
// per-packet QPC timestamp - same "place bytes by their true timeline
// offset" approach ProcessLoopbackWaveIn uses for Game/Chat. WasapiCapture's
// DataAvailable only tells you when the callback fired, which lags the
// device's actual capture instant by its buffer/period; that lag was baking
// itself into the mic's placement as a small, constant sync drift.
[SupportedOSPlatform("windows")]
internal sealed class MicrophoneWaveIn : IWaveIn
{
    private readonly AudioClient _audioClient;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _loggedFirstPacket;

    public MicrophoneWaveIn(MMDevice device)
    {
        _audioClient = device.AudioClient;
        WaveFormat = _audioClient.MixFormat;
        _audioClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 0, 0, WaveFormat, Guid.Empty);
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void StopRecording()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try
        {
            _captureTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Stop is best effort.
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        _audioClient.Dispose();
    }

    private void CaptureLoop(CancellationToken token)
    {
        Exception? stoppedError = null;
        // Same QPC-to-MonotonicClock base pairing as ProcessLoopbackWaveIn -
        // see the comment there for why this survives a mid-session system
        // clock step.
        var utcBase = MonotonicClock.UtcNow;
        var qpcBase100ns = Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency);
        var captureClient = _audioClient.AudioCaptureClient;
        try
        {
            _audioClient.Start();
            while (!token.IsCancellationRequested)
            {
                var packetFrames = captureClient.GetNextPacketSize();
                while (packetFrames > 0)
                {
                    var data = captureClient.GetBuffer(out var frames, out var flags, out _, out var qpcPosition);
                    var bytes = frames * WaveFormat.BlockAlign;
                    var buffer = new byte[bytes];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent) && data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    captureClient.ReleaseBuffer(frames);
                    if (bytes > 0)
                    {
                        if (!_loggedFirstPacket)
                        {
                            _loggedFirstPacket = true;
                            AppLog.Debug($"Mic capture first packet: frames={frames}, bytes={bytes}, silent={flags.HasFlag(AudioClientBufferFlags.Silent)}.");
                        }

                        var packetStartUtc = utcBase + TimeSpan.FromTicks((long)(qpcPosition - qpcBase100ns));
                        DataAvailable?.Invoke(this, new TimestampedWaveInEventArgs(buffer, bytes, packetStartUtc));
                    }

                    packetFrames = captureClient.GetNextPacketSize();
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) stoppedError = error;
        }
        finally
        {
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
}
