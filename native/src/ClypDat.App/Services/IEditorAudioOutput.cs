using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClypDat.App.Services;

internal interface IEditorAudioOutput : IDisposable
{
    PlaybackState PlaybackState { get; }
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    void Init(ISampleProvider provider);
    void Play();
    void Stop();
    long GetPosition();
}

internal sealed class WindowsEditorAudioOutput : IEditorAudioOutput
{
    private readonly WasapiOut _output = new(AudioClientShareMode.Shared, false, 120);
    public PlaybackState PlaybackState => _output.PlaybackState;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped { add => _output.PlaybackStopped += value; remove => _output.PlaybackStopped -= value; }
    public void Init(ISampleProvider provider) => _output.Init(provider);
    public void Play() => _output.Play();
    public void Stop() => _output.Stop();
    public long GetPosition() => _output.GetPosition();
    public void Dispose() => _output.Dispose();
}
