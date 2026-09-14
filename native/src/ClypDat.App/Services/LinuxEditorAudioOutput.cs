using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ClypDat.App.Services;

// Pulse's blocking calls run only on this output thread. The editor transport
// submits state changes; position comes from the server's measured latency.
internal sealed class LinuxEditorAudioOutput : IEditorAudioOutput
{
    private ISampleProvider? _provider;
    private readonly AutoResetEvent _wake = new(false);
    private Task? _writer;
    private volatile bool _disposed, _playing;
    private long _generation, _position;
    public PlaybackState PlaybackState => _playing ? PlaybackState.Playing : PlaybackState.Stopped;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public void Init(ISampleProvider provider)
    {
        if (provider.WaveFormat.SampleRate != 48000 || provider.WaveFormat.Channels != 2) throw new ArgumentException("Editor mixer must provide 48 kHz stereo.");
        _provider = provider; _writer = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
    }
    public void Play() { _playing = true; _wake.Set(); }
    public void Stop() { _playing = false; Interlocked.Increment(ref _generation); _wake.Set(); }
    public long GetPosition() => Interlocked.Read(ref _position);
    private void WriteLoop()
    {
        nint output = 0;
        try
        {
            var spec = new SampleSpec { Format = 5, Rate = 48000, Channels = 2 }; // PA_SAMPLE_FLOAT32LE
            var attributes = new BufferAttributes { MaxLength = uint.MaxValue, TargetLength = 48000 * 8 / 10,
                Prebuffer = 0, MinimumRequest = 480 * 8, FragmentSize = uint.MaxValue };
            output = pa_simple_new(null, "ClypDat", 1, null, "Editor mixer", ref spec, 0, ref attributes, out var error);
            if (output == 0) throw new IOException($"PulseAudio output could not open ({error}).");
            var samples = new float[960];
            long written = 0, generation = 0;
            while (!_disposed)
            {
                var next = Interlocked.Read(ref _generation);
                if (next != generation)
                {
                    if (pa_simple_flush(output, out error) < 0) throw new IOException($"PulseAudio flush failed ({error}).");
                    written = Interlocked.Read(ref _position); generation = next;
                    PlaybackStopped?.Invoke(this, new StoppedEventArgs());
                }
                if (!_playing) { _wake.WaitOne(20); continue; }
                var count = _provider!.Read(samples, 0, samples.Length);
                if (next != Interlocked.Read(ref _generation) || !_playing) continue;
                if (count < samples.Length) Array.Clear(samples, count, samples.Length - count);
                if (pa_simple_write(output, samples, (nuint)(samples.Length * sizeof(float)), out error) < 0)
                    throw new IOException($"PulseAudio output disconnected ({error}).");
                written += samples.Length * sizeof(float);
                var latency = pa_simple_get_latency(output, out error);
                if (latency == ulong.MaxValue) throw new IOException($"PulseAudio clock unavailable ({error}).");
                Interlocked.Exchange(ref _position, Math.Max(Interlocked.Read(ref _position), written - (long)(latency * 48000 * 8 / 1_000_000)));
            }
        }
        catch (Exception error) { _playing = false; PlaybackStopped?.Invoke(this, new StoppedEventArgs(error)); }
        finally { if (output != 0) pa_simple_free(output); }
    }
    public void Dispose() { _disposed = true; _wake.Set(); if (_writer?.Wait(TimeSpan.FromSeconds(2)) == true) _wake.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferAttributes { public uint MaxLength, TargetLength, Prebuffer, MinimumRequest, FragmentSize; }
    [DllImport("libpulse-simple.so.0")] private static extern nint pa_simple_new(string? server, string name, int direction, string? device, string streamName, ref SampleSpec spec, nint map, ref BufferAttributes attributes, out int error);
    [DllImport("libpulse-simple.so.0")] private static extern int pa_simple_write(nint output, float[] samples, nuint bytes, out int error);
    [DllImport("libpulse-simple.so.0")] private static extern ulong pa_simple_get_latency(nint output, out int error);
    [DllImport("libpulse-simple.so.0")] private static extern int pa_simple_flush(nint output, out int error);
    [DllImport("libpulse-simple.so.0")] private static extern void pa_simple_free(nint output);
}
