using NAudio.Wave;

namespace Eve.App.Services;

public static class ClipNotificationSound
{
    public static void Play(string volumeLevel)
    {
        var gain = volumeLevel switch
        {
            "Low" => 0.035f,
            "High" => 0.16f,
            _ => 0.08f
        };

        try
        {
            var waveOut = new WaveOutEvent();
            var provider = new ClipChimeSampleProvider(gain);
            waveOut.Init(provider);
            waveOut.PlaybackStopped += (_, _) =>
            {
                waveOut.Dispose();
            };
            waveOut.Play();
        }
        catch (Exception error)
        {
            AppLog.Error("Clip notification sound failed to play", error);
        }
    }

    // A single warm tone with a smooth raised-cosine envelope (no hard attack/
    // release, so there's no click or harsh edge) plus a much quieter octave
    // overtone for a rounder, softer timbre than a plain sine beep.
    private sealed class ClipChimeSampleProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private const double Fundamental = 523.25; // C5 - soft, not piercing
        private readonly float _gain;
        private int _sampleIndex;
        private readonly int _totalSamples;

        public ClipChimeSampleProvider(float gain)
        {
            _gain = gain;
            _totalSamples = (int)(SampleRate * 0.42);
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count && _sampleIndex < _totalSamples)
            {
                buffer[offset + written] = SampleAt(_sampleIndex);
                _sampleIndex++;
                written++;
            }

            return written;
        }

        private float SampleAt(int sampleIndex)
        {
            var t = sampleIndex / (double)SampleRate;
            var envelope = Envelope(sampleIndex);
            var tone = Math.Sin(2 * Math.PI * Fundamental * t) +
                       0.25 * Math.Sin(2 * Math.PI * Fundamental * 2 * t);
            return (float)(tone * envelope * _gain);
        }

        private double Envelope(int sampleIndex)
        {
            // Raised-cosine (Hann-style) envelope over the whole note: eases in,
            // holds briefly, eases out - nothing sudden anywhere in the sound.
            var progress = sampleIndex / (double)_totalSamples;
            return 0.5 * (1 - Math.Cos(2 * Math.PI * Math.Min(progress, 1.0)));
        }
    }
}
